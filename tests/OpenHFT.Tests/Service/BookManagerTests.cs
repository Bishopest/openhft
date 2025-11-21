using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OpenHFT.Core.Books;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Service;

namespace OpenHFT.Tests.Service;

[TestFixtureSource(nameof(GetTestInstruments))]
public class BookManagerTests_Linear
{
    private Mock<ILogger<BookManager>> _mockLogger;
    private Mock<IOrderRouter> _mockOrderRouter;
    private ServiceProvider _serviceProvider = null!;
    private BookManager _bookManager;
    private readonly Instrument _instrument;
    private const string TestBookName = "BTC";
    private InstrumentRepository _repository;
    public static IEnumerable<Instrument> GetTestInstruments()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "TempRepoForTests");
        Directory.CreateDirectory(testDir);
        var csvContent = @"instrument_id,market,symbol,type,base_currency,quote_currency,minimum_price_variation,lot_size,contract_multiplier,minimum_order_size
    1,binance,BTCUSDT,perpetualfuture,BTCUSDT,USDT,1,0.1,0.001,BTCUSDT
    4,bitmex,XBTUSDT,perpetualfuture,XBTUSDT,USDT,0.000001,0.1,100,XBTUSDT,100
    6,bitmex,BCHUSDT,perpetualfuture,BCHUSDT,USDT,0.00001,0.05,1000,BCHUSDT,1000";
        File.WriteAllText(Path.Combine(testDir, "instruments.csv"), csvContent);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new[] { new KeyValuePair<string, string>("dataFolder", testDir) }).Build();
        var repo = new InstrumentRepository(new NullLogger<InstrumentRepository>(), config);
        return repo.GetAll();
    }

    public BookManagerTests_Linear(Instrument instrument)
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

        // IInstrumentRepository는 Mocking하여 테스트 격리성을 높입니다.
        var mockRepo = new Mock<IInstrumentRepository>();
        mockRepo.Setup(r => r.GetById(_instrument.InstrumentId)).Returns(_instrument);

        services.AddSingleton(mockOrderRouter.Object);
        services.AddSingleton(mockRepo.Object);

        _serviceProvider = services.BuildServiceProvider();
        _bookManager = new BookManager(
            _serviceProvider.GetRequiredService<ILogger<BookManager>>(),
            mockOrderRouter.Object,
            mockRepo.Object,
            mockBookRepository.Object
        );

        // _elements에 현재 테스트의 Instrument에 대한 초기 BookElement만 추가합니다.
        var elementsField = typeof(BookManager).GetField("_elements", BindingFlags.NonPublic | BindingFlags.Instance);
        var elementsDict = elementsField.GetValue(_bookManager) as System.Collections.Concurrent.ConcurrentDictionary<int, BookElement>;
        elementsDict[_instrument.InstrumentId] = new BookElement(TestBookName, _instrument.InstrumentId, Price.FromDecimal(0m), Quantity.FromDecimal(0m), CurrencyAmount.FromDecimal(0m, _instrument.DenominationCurrency), CurrencyAmount.FromDecimal(0m, Currency.USDT), 0);
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

    private void SimulateFill(Fill fill)
    {
        var method = typeof(BookManager).GetMethod("OnOrderFilled", BindingFlags.NonPublic | BindingFlags.Instance);
        method.Invoke(_bookManager, new object[] { this, fill });
    }

    [Test]
    public void OnOrderFilled_WithInitialBuy_ShouldUpdateElementCorrectly()
    {
        // Arrange
        var fill = new Fill(_instrument.InstrumentId, 1, "exo1", "exec1", Side.Buy,
                            Price.FromDecimal(100m), Quantity.FromDecimal(10m), 0);

        // Act
        SimulateFill(fill);

        // Assert
        var element = _bookManager.GetBookElement(_instrument.InstrumentId);
        var multiplier = _instrument is CryptoFuture cf ? cf.Multiplier : 1m;
        element.Size.ToDecimal().Should().Be(10m);
        element.AvgPrice.ToDecimal().Should().Be(100m);
        element.RealizedPnL.Amount.Should().Be(0m * multiplier);
        element.VolumeInUsdt.Amount.Should().Be(1000m * multiplier); // 100 * 10
    }

    [Test]
    public void OnOrderFilled_WithAdditionalBuy_ShouldUpdateAveragePrice()
    {
        // Arrange
        var fill1 = new Fill(_instrument.InstrumentId, 1, "exo1", "exec1", Side.Buy, Price.FromDecimal(100m), Quantity.FromDecimal(10m), 0);
        SimulateFill(fill1);

        var fill2 = new Fill(_instrument.InstrumentId, 2, "exo2", "exec2", Side.Buy, Price.FromDecimal(90m), Quantity.FromDecimal(10m), 0);
        // Expected Avg Price = (10 * 100 + 10 * 90) / (10 + 10) = 95

        // Act
        SimulateFill(fill2);

        // Assert
        var element = _bookManager.GetBookElement(_instrument.InstrumentId);
        element.Size.ToDecimal().Should().Be(20m);
        element.AvgPrice.ToDecimal().Should().Be(95m);
        element.RealizedPnL.Amount.Should().Be(0m);
    }

    [Test]
    public void OnOrderFilled_WithPartialSell_ShouldCalculateRealizedPnl()
    {
        // Arrange
        var fill1 = new Fill(_instrument.InstrumentId, 1, "exo1", "exec1", Side.Buy, Price.FromDecimal(100m), Quantity.FromDecimal(10m), 0);
        SimulateFill(fill1);

        var fill2 = new Fill(_instrument.InstrumentId, 2, "exo2", "exec2", Side.Sell, Price.FromDecimal(110m), Quantity.FromDecimal(4m), 0);
        // PnL = (110 - 100) * 4 = 40

        // Act
        SimulateFill(fill2);

        // Assert
        var element = _bookManager.GetBookElement(_instrument.InstrumentId);
        var multiplier = _instrument is CryptoFuture cf ? cf.Multiplier : 1m;
        element.Size.ToDecimal().Should().Be(6m); // 10 - 4
        element.AvgPrice.ToDecimal().Should().Be(100m, "Avg price should not change on partial close.");
        element.RealizedPnL.Amount.Should().Be(40m * multiplier);
    }

    [Test]
    public void OnOrderFilled_WithPositionFlipFromLongToShort_ShouldUpdateCorrectly()
    {
        // Arrange
        var fill1 = new Fill(_instrument.InstrumentId, 1, "exo1", "exec1", Side.Buy, Price.FromDecimal(100m), Quantity.FromDecimal(10m), 0);
        SimulateFill(fill1);

        var fill2 = new Fill(_instrument.InstrumentId, 2, "exo2", "exec2", Side.Sell, Price.FromDecimal(120m), Quantity.FromDecimal(15m), 0);
        // Position closed: 10 contracts. Realized PnL = (120 - 100) * 10 = 200
        // New position: -5 contracts @ 120

        // Act
        SimulateFill(fill2);

        // Assert
        var element = _bookManager.GetBookElement(_instrument.InstrumentId);
        var multiplier = _instrument is CryptoFuture cf ? cf.Multiplier : 1m;
        element.Size.ToDecimal().Should().Be(-5m); // 10 - 15
        element.AvgPrice.ToDecimal().Should().Be(120m, "Avg price should reset to the flipping trade's price.");
        element.RealizedPnL.Amount.Should().Be(200m * multiplier);
    }

    [Test]
    public void OnOrderFilled_WithFillForUnmanagedInstrument_ShouldDoNothing()
    {
        // Arrange
        var unmanagedFill = new Fill(999, 1, "exo1", "exec1", Side.Buy, Price.FromDecimal(100m), Quantity.FromDecimal(10m), 0);

        // Act
        SimulateFill(unmanagedFill);

        // Assert
        var element = _bookManager.GetBookElement(_instrument.InstrumentId);
        // Element should still be in its initial zero state
        element.Size.ToTicks().Should().Be(0);

        // Event should not have been fired
        // This requires mocking the event handler subscription, which is more complex.
        // For now, we verify the state change didn't happen.
    }
}