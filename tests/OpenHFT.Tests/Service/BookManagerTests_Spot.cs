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
public class BookManagerTests_Spot
{
    private Mock<ILogger<BookManager>> _mockLogger;
    private Mock<IOrderRouter> _mockOrderRouter;
    private Mock<IFxRateService> _mockFxRateService;
    private ServiceProvider _serviceProvider = null!;
    private BookManager _bookManager;
    private readonly Instrument _instrument;
    private const string TestBookName = "ETH_SPOT";
    private InstrumentRepository _repository;

    public static IEnumerable<Instrument> GetTestInstruments()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "TempRepoForTests");
        Directory.CreateDirectory(testDir);
        var csvContent = @"instrument_id,market,symbol,type,base_currency,quote_currency,contract_multiplier,minimum_price_variation,lot_size,minimum_order_size
    1,BITHUMB,KRW-ETH,Spot,ETH,KRW,1000,0.0001,1,0.002";

        File.WriteAllText(Path.Combine(testDir, "instruments.csv"), csvContent);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new[] { new KeyValuePair<string, string>("dataFolder", testDir) }).Build();
        var repo = new InstrumentRepository(new NullLogger<InstrumentRepository>(), config);
        return repo.GetAll();
    }

    public BookManagerTests_Spot(Instrument instrument)
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
        services.AddSingleton(_mockFxRateService.Object);

        _serviceProvider = services.BuildServiceProvider();
        // ------------------------------------
        // 1. 테스트에 사용할 BookConfig 데이터 정의
        // ------------------------------------
        var testConfigs = new List<BookConfig>
        {
            new BookConfig {  BookName = "ETH_SPOT", Hedgeable = true },
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
        elementsDict[(TestBookName, _instrument.InstrumentId)] = new BookElement(TestBookName, _instrument.InstrumentId, Price.FromDecimal(0m), Quantity.FromDecimal(0m), CurrencyAmount.FromDecimal(0m, Currency.USDT), CurrencyAmount.FromDecimal(0m, Currency.USDT), 0);
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
        _mockFxRateService.Setup(s => s.Convert(It.IsAny<CurrencyAmount>(), target))
            .Returns<CurrencyAmount, Currency>((source, tgt) =>
            {
                if (source.Currency == tgt) return source;

                decimal converted = 0m;
                // KRW -> USDT (Multiply)
                if (source.Currency == Currency.KRW && tgt == Currency.USDT)
                    converted = source.Amount / rate;
                // USDT -> BTC (Divide)
                else if (source.Currency == Currency.USDT && tgt == Currency.KRW)
                    converted = source.Amount * rate;

                return new CurrencyAmount(converted, tgt);
            });
    }

    private void SimulateFill(Fill fill)
    {
        var method = typeof(BookManager).GetMethod("OnOrderFilled", BindingFlags.NonPublic | BindingFlags.Instance);
        method.Invoke(_bookManager, new object[] { this, fill });
    }

    [Test]
    public void OnOrderFilled_Spot_InitialBuy_ShouldUpdateElementCorrectly()
    {
        // Arrange
        var fill = new Fill(_instrument.InstrumentId, TestBookName, 1, "exo1", "exec1", Side.Buy,
                            Price.FromDecimal(4_000_000m), Quantity.FromDecimal(2.5m), 0);
        // Set fx rate
        SetupFxRate(2000m, Currency.USDT);

        // Act
        SimulateFill(fill);

        // Assert
        var element = _bookManager.GetBookElement(TestBookName, _instrument.InstrumentId);
        element.Size.ToDecimal().Should().Be(2.5m);
        element.AvgPrice.ToDecimal().Should().Be(4_000_000m);
        element.RealizedPnL.Amount.Should().Be(0m);
        // Volume = Price * Quantity = 4,000,000 * 2.5 = 10,000,000
        element.Volume.Amount.Should().Be(5000m);
    }

    [Test]
    public void OnOrderFilled_Spot_AdditionalBuy_ShouldUpdateAveragePrice()
    {
        // Set fx rate
        SetupFxRate(2000m, Currency.USDT);

        // Arrange
        SimulateFill(new Fill(_instrument.InstrumentId, TestBookName, 1, "exo1", "exec1", Side.Buy, Price.FromDecimal(4_000_000m), Quantity.FromDecimal(1m), 0));
        SimulateFill(new Fill(_instrument.InstrumentId, TestBookName, 2, "exo2", "exec2", Side.Buy, Price.FromDecimal(4_200_000m), Quantity.FromDecimal(1m), 0));
        // Expected Avg Price = (1*4,000,000 + 1*4,200,000) / (1 + 1) = 4,100,000

        // Act
        // No additional action needed, fills are already simulated.

        // Assert
        var element = _bookManager.GetBookElement(TestBookName, _instrument.InstrumentId);
        element.Size.ToDecimal().Should().Be(2m);
        element.AvgPrice.ToDecimal().Should().Be(4_100_000m);
        element.RealizedPnL.Amount.Should().Be(0m);
    }

    [Test]
    public void OnOrderFilled_Spot_PartialSell_ShouldCalculateRealizedPnl()
    {
        // Set fx rate
        SetupFxRate(2000m, Currency.USDT);

        // Arrange
        SimulateFill(new Fill(_instrument.InstrumentId, TestBookName, 1, "exo1", "exec1", Side.Buy, Price.FromDecimal(4_000_000m), Quantity.FromDecimal(10m), 0));

        var closingFill = new Fill(_instrument.InstrumentId, TestBookName, 2, "exo2", "exec2", Side.Sell, Price.FromDecimal(4_100_000m), Quantity.FromDecimal(3m), 0);
        // Expected PnL = (Sell Price - Avg Buy Price) * Sell Qty
        // = (4,100,000 - 4,000,000) * 3 = 300,000
        // (convert to USDT) 300,000 / 2,000 = 150 

        // Act
        SimulateFill(closingFill);

        // Assert
        var element = _bookManager.GetBookElement(TestBookName, _instrument.InstrumentId);
        element.Size.ToDecimal().Should().Be(7m); // 10 - 3
        element.AvgPrice.ToDecimal().Should().Be(4_000_000m, "Avg price should not change on partial close.");
        element.RealizedPnL.Amount.Should().Be(150);
    }

    [Test]
    public void OnOrderFilled_Spot_PositionFlip_ShouldUpdateCorrectly()
    {
        // Set fx rate
        SetupFxRate(2000m, Currency.USDT);

        // Arrange
        SimulateFill(new Fill(_instrument.InstrumentId, TestBookName, 1, "exo1", "exec1", Side.Buy, Price.FromDecimal(4_000_000m), Quantity.FromDecimal(5m), 0));

        var flippingFill = new Fill(_instrument.InstrumentId, TestBookName, 2, "exo2", "exec2", Side.Sell, Price.FromDecimal(4_200_000m), Quantity.FromDecimal(8m), 0);
        // PnL from closing 5 ETH = (4,200,000 - 4,000,000) * 5 = 1,000,000
        // (convert to USDT) 1,000,000 / 2,000 = 500
        // New position = -3 ETH @ 4,200,000

        // Act
        SimulateFill(flippingFill);

        // Assert
        var element = _bookManager.GetBookElement(TestBookName, _instrument.InstrumentId);
        element.Size.ToDecimal().Should().Be(-3m); // 5 - 8
        element.AvgPrice.ToDecimal().Should().Be(4_200_000m, "Avg price should reset to the flipping trade's price.");
        element.RealizedPnL.Amount.Should().Be(500m);
    }
}