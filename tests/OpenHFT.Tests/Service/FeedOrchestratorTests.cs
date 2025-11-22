using Disruptor.Dsl;
using DotNetEnv;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
using OpenHFT.Feed;
using OpenHFT.Feed.Adapters;
using OpenHFT.Feed.Interfaces;
using OpenHFT.Gateway;
using OpenHFT.Gateway.Interfaces;
using OpenHFT.Processing;
using OpenHFT.Service;

namespace OpenHFT.Tests.Service
{
    public class FeedOrchestratorTests
    {
        private ServiceProvider _serviceProvider;
        private IFeedAdapter _bitmexAdapter;
        private FeedOrchestrator _orchestrator;
        private string _testDirectory;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            Env.Load();
            Env.TraversePath().Load();
            var apiKey = Environment.GetEnvironmentVariable("BITMEX_TESTNET_API_KEY");
            var apiSecret = Environment.GetEnvironmentVariable("BITMEX_TESTNET_API_SECRET");
            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
                Assert.Ignore("BITMEX Testnet credentials not set.");

            var services = new ServiceCollection();

            // --- 1. DI 구성 ---
            services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
            _testDirectory = Path.Combine(Path.GetTempPath(), "QuotingInstanceManagerTests", Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDirectory);
            var csvContent = @"instrument_id,market,symbol,type,base_currency,quote_currency,minimum_price_variation,lot_size,contract_multiplier,minimum_order_size
1,BITMEX,XBTUSD,PerpetualFuture,XBT,USD,0.5,1,1,1
2,BINANCE,BTCUSDT,PerpetualFuture,BTC,USDT,0.01,0.001,1,0.001";
            File.WriteAllText(Path.Combine(_testDirectory, "instruments.csv"), csvContent);

            var inMemoryConfig = new Dictionary<string, string> { { "dataFolder", _testDirectory },
                { "BITMEX_TESTNET_API_KEY", apiKey },
                { "BITMEX_TESTNET_API_SECRET", apiSecret }
            };
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemoryConfig).Build();

            services.AddSingleton(configuration);
            services.AddSingleton<IInstrumentRepository, InstrumentRepository>();

            services.AddSingleton<IOrderRouter, OrderRouter>();
            services.AddSingleton(new SubscriptionConfig());
            services.AddSingleton<MarketDataDistributor>();
            services.AddSingleton<IOrderUpdateHandler, OrderUpdateDistributor>();
            services.AddSingleton(provider =>
               {
                   var disruptor = new Disruptor<MarketDataEventWrapper>(() => new MarketDataEventWrapper(), 1024);
                   disruptor.HandleEventsWith(provider.GetRequiredService<MarketDataDistributor>());
                   return disruptor;
               });

            services.AddSingleton(provider =>
            {
                var disruptor = new Disruptor<OrderStatusReportWrapper>(() => new OrderStatusReportWrapper(), 1024);
                disruptor.HandleEventsWith(provider.GetRequiredService<IOrderUpdateHandler>());
                return disruptor;
            });
            // 실제 BitmexAdapter 등록 (Testnet)
            services.AddSingleton<IFeedAdapter>(provider => new BitmexAdapter(
                provider.GetRequiredService<ILogger<BitmexAdapter>>(),
                ProductType.PerpetualFuture,
                provider.GetRequiredService<IInstrumentRepository>(),
                ExecutionMode.Testnet
            ));

            services.AddSingleton<IFeedHandler, FeedHandler>();
            services.AddSingleton<ISubscriptionManager, SubscriptionManager>();
            services.AddSingleton<IFeedAdapterRegistry, FeedAdapterRegistry>();
            services.AddSingleton<ITimeSyncManager, NullTimeSyncManager>(); // Mock or Null implementation

            // 실제 FeedOrchestrator 등록
            services.AddSingleton<FeedOrchestrator>();

            _serviceProvider = services.BuildServiceProvider();
            _orchestrator = _serviceProvider.GetRequiredService<FeedOrchestrator>();
            _bitmexAdapter = _serviceProvider.GetRequiredService<IFeedAdapterRegistry>().GetAdapter(ExchangeEnum.BITMEX, ProductType.PerpetualFuture);
        }

        [OneTimeTearDown]
        public async Task OneTimeTearDown()
        {
            await _orchestrator.StopAsync(CancellationToken.None);
            _serviceProvider?.Dispose();
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, true);
            }
        }

        [Test]
        public async Task When_ConnectionIsForciblyDropped_Should_ReconnectAndReAuthenticate()
        {
            // --- Arrange ---
            // 이벤트 발생을 기다리기 위한 TaskCompletionSource 설정
            var initialConnectionTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var initialAuthTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disconnectionTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reconnectionTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reAuthTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            void ConnectionHandler(object s, ConnectionStateChangedEventArgs e)
            {
                if (e.IsConnected && !initialConnectionTcs.Task.IsCompleted)
                    initialConnectionTcs.TrySetResult();
                else if (!e.IsConnected && initialConnectionTcs.Task.IsCompleted)
                    disconnectionTcs.TrySetResult();
                else if (e.IsConnected && disconnectionTcs.Task.IsCompleted)
                    reconnectionTcs.TrySetResult();
            }

            void AuthHandler(object s, AuthenticationEventArgs e)
            {
                if (e.IsAuthenticated && !initialAuthTcs.Task.IsCompleted)
                    initialAuthTcs.TrySetResult();
                else if (e.IsAuthenticated && reconnectionTcs.Task.IsCompleted)
                    reAuthTcs.TrySetResult();
            }

            _bitmexAdapter.ConnectionStateChanged += ConnectionHandler;
            // BaseAuthFeedAdapter에서 이벤트가 발생한다고 가정
            if (_bitmexAdapter is BaseAuthFeedAdapter authAdapter)
            {
                authAdapter.AuthenticationStateChanged += AuthHandler;
            }

            // --- Act 1: 초기 시작 ---
            TestContext.WriteLine("Step 1: Starting orchestrator and waiting for initial connection and authentication...");
            await _orchestrator.StartAsync(CancellationToken.None);

            // 초기 연결 및 인증이 완료될 때까지 최대 30초 대기
            await Task.WhenAll(initialConnectionTcs.Task, initialAuthTcs.Task).WaitAsync(TimeSpan.FromSeconds(30));

            // Assert 1: 초기 상태 검증
            _bitmexAdapter.IsConnected.Should().BeTrue();
            TestContext.WriteLine(" -> Initial connection and authentication successful.");

            // --- Act 2: 연결 강제 중단 ---
            TestContext.WriteLine("\nStep 2: Simulating connection drop to trigger reconnect logic...");

            // DisconnectAsync 대신, 비정상 종료를 시뮬레이션합니다.
            (_bitmexAdapter as BaseFeedAdapter)?.SimulateConnectionDrop();
            // 연결 끊김 이벤트가 발생할 때까지 대기
            await disconnectionTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            TestContext.WriteLine(" -> Disconnection detected.");

            // --- Assert 2: 재연결 및 재인증 검증 ---
            TestContext.WriteLine("\nStep 3: Waiting for automatic reconnection and re-authentication...");

            // 재연결 및 재인증이 완료될 때까지 대기
            await Task.WhenAll(reconnectionTcs.Task, reAuthTcs.Task).WaitAsync(TimeSpan.FromSeconds(30));

            _bitmexAdapter.IsConnected.Should().BeTrue("because the adapter should have reconnected automatically.");
            TestContext.WriteLine(" -> Reconnection successful.");
            TestContext.WriteLine(" -> Re-authentication successful.");

            // --- Cleanup ---
            _bitmexAdapter.ConnectionStateChanged -= ConnectionHandler;
            if (_bitmexAdapter is BaseAuthFeedAdapter authAdapterCleanup)
            {
                authAdapterCleanup.AuthenticationStateChanged -= AuthHandler;
            }
        }
    }
}