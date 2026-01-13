using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using OpenHFT.Core.Books;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Service;

namespace OpenHFT.Tests.Service;

[TestFixtureSource(nameof(GetTestInstruments))]
public class BookManagerTests_Inverse
{
    private Mock<ILogger<BookManager>> _mockLogger;
    private Mock<IOrderRouter> _mockOrderRouter;
    private Mock<IFxRateService> _mockFxRateService;
    private ServiceProvider _serviceProvider = null!;
    private BookManager _bookManager;
    private readonly Instrument _instrument;
    private const string TestBookName = "BTC";
    private InstrumentRepository _repository;
    public static IEnumerable<Instrument> GetTestInstruments()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "TempRepoForTests");
        Directory.CreateDirectory(testDir);
        var csvContent = @"instrument_id,market,symbol,type,base_currency,quote_currency,contract_multiplier,minimum_price_variation,lot_size,minimum_order_size
    3109,bitmex,XBTUSD,perpetualfuture,XBT,USD,1,0.1,100,XBTUSD,100";
        File.WriteAllText(Path.Combine(testDir, "instruments.csv"), csvContent);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new[] { new KeyValuePair<string, string>("dataFolder", testDir) }).Build();
        var repo = new InstrumentRepository(new NullLogger<InstrumentRepository>(), config);
        return repo.GetAll();
    }

    public BookManagerTests_Inverse(Instrument instrument)
    {
        _instrument = instrument;
    }

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole());

        var mockOrderRouter = new Mock<IOrderRouter>();
        var mockBookRepository = new Mock<IBookRepository>();
        _mockFxRateService = new Mock<IFxRateService>();
        // IInstrumentRepository는 Mocking하여 테스트 격리성을 높입니다.
        var mockRepo = new Mock<IInstrumentRepository>();
        mockRepo.Setup(r => r.GetById(_instrument.InstrumentId)).Returns(_instrument);

        services.AddSingleton(mockOrderRouter.Object);
        services.AddSingleton(mockRepo.Object);

        _serviceProvider = services.BuildServiceProvider();
        // ------------------------------------
        // 1. 테스트에 사용할 BookConfig 데이터 정의
        // ------------------------------------
        var testConfigs = new List<BookConfig>
        {
            new BookConfig {  BookName = "BTC", Hedgeable = true },
        };

        // ------------------------------------
        // 2. IOptions<List<BookConfig>> Mock 객체 생성
        // ------------------------------------
        var mockOptions = new Mock<IOptions<List<BookConfig>>>();

        // 3. Mock의 .Value 속성이 위에서 정의한 testConfigs 리스트를 반환하도록 설정
        mockOptions.SetupGet(o => o.Value).Returns(testConfigs);
        var inMemorySettings = new Dictionary<string, string>
        {
            { "omsIdentifier", "test-oms" }
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();
        _bookManager = new BookManager(
            _serviceProvider.GetRequiredService<ILogger<BookManager>>(),
            mockOrderRouter.Object,
            mockRepo.Object,
            mockBookRepository.Object,
            configuration,
            mockOptions.Object,
            _mockFxRateService.Object
        );

        // _elements에 현재 테스트의 Instrument에 대한 초기 BookElement만 추가합니다.
        var elementsField = typeof(BookManager).GetField("_elements", BindingFlags.NonPublic | BindingFlags.Instance);
        var elementsDict = elementsField.GetValue(_bookManager) as System.Collections.Concurrent.ConcurrentDictionary<(string BookName, int InstrumentId), BookElement>;
        elementsDict[(TestBookName, _instrument.InstrumentId)] = new BookElement(TestBookName, _instrument.InstrumentId, Price.FromDecimal(0m), Quantity.FromDecimal(0m), CurrencyAmount.FromDecimal(0m, _instrument.DenominationCurrency), CurrencyAmount.FromDecimal(0m, Currency.USDT), 0);
    }

    [TearDown]
    public void TearDown()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), "TempRepoForTests", Guid.NewGuid().ToString());
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, true);
        }
    }

    private void SetupFxRate(decimal rate, Currency target)
    {
        // rate: BTC/USDT price (50000)
        // Convert(amount, target) Logic simulation
        _mockFxRateService.Setup(s => s.Convert(It.IsAny<CurrencyAmount>(), target))
            .Returns<CurrencyAmount, Currency>((source, tgt) =>
            {
                if (source.Currency == tgt) return source;

                decimal converted = 0m;
                // BTC -> USDT (Multiply)
                if (source.Currency == Currency.BTC && tgt == Currency.USDT)
                    converted = source.Amount * rate;
                // USDT -> BTC (Divide)
                else if (source.Currency == Currency.USDT && tgt == Currency.BTC)
                    converted = source.Amount / rate;

                return new CurrencyAmount(converted, tgt);
            });
    }

    private void SimulateFill(Fill fill)
    {
        var method = typeof(BookManager).GetMethod("OnOrderFilled", BindingFlags.NonPublic | BindingFlags.Instance);
        method.Invoke(_bookManager, new object[] { this, fill });
    }

    [Test]
    public void OnOrderFilled_WithInitialBuy_ShouldUpdateElementCorrectly()
    {
        // Arrange
        var fill = new Fill(_instrument.InstrumentId, "test", 1, "exo1", "exec1", Side.Buy,
                            Price.FromDecimal(91100m), Quantity.FromDecimal(100m), 0);

        SetupFxRate(50000m, Currency.USDT);

        // Act
        SimulateFill(fill);

        // Assert
        var element = _bookManager.GetBookElement("test", _instrument.InstrumentId);
        element.Size.ToDecimal().Should().Be(100m);
        element.AvgPrice.ToDecimal().Should().Be(91100m);
        element.RealizedPnL.Amount.Should().Be(0m);
        element.Volume.Amount.Should().BeApproximately(54.88m, 0.01m); // 100 * 10
    }

    [Test]
    public void OnOrderFilled_WithAdditionalBuy_ShouldUpdateAveragePrice()
    {
        // Arrange
        var fill1 = new Fill(_instrument.InstrumentId, "test", 1, "exo1", "exec1", Side.Buy, Price.FromDecimal(10000m), Quantity.FromDecimal(100m), 0);
        SimulateFill(fill1);

        var fill2 = new Fill(_instrument.InstrumentId, "test", 2, "exo2", "exec2", Side.Buy, Price.FromDecimal(9000m), Quantity.FromDecimal(100m), 0);
        // Expected Avg Price = (10 * 100 + 10 * 90) / (10 + 10) = 95

        // Act
        SimulateFill(fill2);

        // Assert
        var element = _bookManager.GetBookElement("test", _instrument.InstrumentId);
        element.Size.ToDecimal().Should().Be(200m);
        element.AvgPrice.ToDecimal().Should().BeApproximately(9473.68m, 0.01m); // (10000 * 9000) * (100 + 100) / (10000 * 100 + 9000 * 100/)
        element.RealizedPnL.Amount.Should().Be(0m);
    }

    [Test]
    public void OnOrderFilled_WithPartialSell_ShouldCalculateRealizedPnl()
    {
        // Arrange
        SetupFxRate(11000m, Currency.USDT);
        var fill1 = new Fill(_instrument.InstrumentId, "test", 1, "exo1", "exec1", Side.Buy, Price.FromDecimal(10000m), Quantity.FromDecimal(100m), 0);

        SimulateFill(fill1);

        var fill2 = new Fill(_instrument.InstrumentId, "test", 2, "exo2", "exec2", Side.Sell, Price.FromDecimal(11000m), Quantity.FromDecimal(40m), 0);
        // PnL = (1/10000 - 1/11000) * 40 * 1 = 0.00036 

        // Act
        SimulateFill(fill2);

        // Assert
        var element = _bookManager.GetBookElement("test", _instrument.InstrumentId);
        element.Size.ToDecimal().Should().Be(60m); // 100 - 40
        element.AvgPrice.ToDecimal().Should().Be(10000m, "Avg price should not change on partial close.");
        element.RealizedPnL.Amount.Should().BeApproximately(4m, 0.1m); // (1/10000 - 1/11000) * 40m * 11000(in USDT)
    }

    [Test]
    public void OnOrderFilled_WithPartialSell_Twice_ShouldCalculateRealizedPnl()
    {
        // Arrange
        SetupFxRate(549.5m, Currency.USDT);
        var fill1 = new Fill(_instrument.InstrumentId, "test", 1, "exo1", "exec1", Side.Sell, Price.FromDecimal(549.15m), Quantity.FromDecimal(875000m), 0);
        var fill2 = new Fill(_instrument.InstrumentId, "test", 2, "exo2", "exec2", Side.Sell, Price.FromDecimal(549.15m), Quantity.FromDecimal(135000m), 0);
        var fill3 = new Fill(_instrument.InstrumentId, "test", 3, "exo3", "exec3", Side.Buy, Price.FromDecimal(549.5m), Quantity.FromDecimal(1010000m), 0);
        // PnL (1/549.5 - 1/549.15) * 1010000 = -1.1714m

        // Act
        SimulateFill(fill1);
        SimulateFill(fill2);
        SimulateFill(fill3);

        // Assert
        var element = _bookManager.GetBookElement("test", _instrument.InstrumentId);
        element.Size.ToDecimal().Should().Be(0m); // 10 - 4
        element.AvgPrice.ToDecimal().Should().Be(0m, "Avg price should not change on partial close.");
        element.RealizedPnL.Amount.Should().BeApproximately(-643.7221m, 0.0001m);
    }

    [Test]
    public void OnOrderFilled_WithPositionFlipFromLongToShort_ShouldUpdateCorrectly()
    {
        // Arrange
        SetupFxRate(120m, Currency.USDT);
        var fill1 = new Fill(_instrument.InstrumentId, "test", 1, "exo1", "exec1", Side.Buy, Price.FromDecimal(100m), Quantity.FromDecimal(10m), 0);
        SimulateFill(fill1);

        var fill2 = new Fill(_instrument.InstrumentId, "test", 2, "exo2", "exec2", Side.Sell, Price.FromDecimal(120m), Quantity.FromDecimal(15m), 0);
        // Position closed: 10 contracts. Realized PnL = (1/100 - 1/120) * 10 * 120 = 2m
        // New position: -5 contracts @ 120

        // Act
        SimulateFill(fill2);

        // Assert
        var element = _bookManager.GetBookElement("test", _instrument.InstrumentId);
        element.Size.ToDecimal().Should().Be(-5m); // 10 - 15
        element.AvgPrice.ToDecimal().Should().Be(120m, "Avg price should reset to the flipping trade's price.");
        element.RealizedPnL.Amount.Should().BeApproximately(2m, 0.001m);
    }

    [Test]
    public void OnOrderFilled_WithFillForUnmanagedInstrument_ShouldDoNothing()
    {
        // Arrange
        var unmanagedFill = new Fill(999, "test", 1, "exo1", "exec1", Side.Buy, Price.FromDecimal(100m), Quantity.FromDecimal(10m), 0);

        // Act
        SimulateFill(unmanagedFill);

        // Assert
        var element = _bookManager.GetBookElement("test", _instrument.InstrumentId);
        // Element should still be in its initial zero state
        element.Size.ToTicks().Should().Be(0);

        // Event should not have been fired
        // This requires mocking the event handler subscription, which is more complex.
        // For now, we verify the state change didn't happen.
    }
}