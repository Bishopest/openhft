using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;

namespace OpenHFT.Tests.Core.Instruments;

[TestFixture]
public class InstrumentRepositoryTests
{
    private string _testDirectory;
    private InstrumentRepository _repository;

    [SetUp]
    public void SetUp()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "InstrumentRepositoryTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
        _repository = new InstrumentRepository(new NullLogger<InstrumentRepository>());
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    private string CreateTestCsvFile(string content)
    {
        var filePath = Path.Combine(_testDirectory, "instruments.csv");
        File.WriteAllText(filePath, content);
        return filePath;
    }

    [Test]
    public void LoadFromCsv_ValidFile_LoadsInstrumentsCorrectly()
    {
        // Arrange
        var csvContent = @"market,symbol,type,base_currency,quote_currency,minimum_price_variation,lot_size,contract_multiplier,minimum_order_size
BINANCE,BTCUSDT,Spot,BTC,USDT,0.01,0.00001,1,10
BINANCE,ETHUSDT,Spot,ETH,USDT,0.01,0.0001,1,10
BINANCE,BTCUSDT,PerpetualFuture,BTC,USDT,0.1,0.001,1,0.001";
        var filePath = CreateTestCsvFile(csvContent);

        // Act
        _repository.LoadFromCsv(filePath);
        var allInstruments = _repository.GetAll().ToList();

        // Assert
        Assert.That(allInstruments.Count, Is.EqualTo(3));

        var btcSpot = _repository.FindBySymbol("BTCUSDT", ProductType.Spot, ExchangeEnum.BINANCE);
        Assert.That(btcSpot, Is.Not.Null);
        Assert.That(btcSpot.Symbol, Is.EqualTo("BTCUSDT"));
        Assert.That(btcSpot.ProductType, Is.EqualTo(ProductType.Spot));
        Assert.That(btcSpot.TickSize, Is.EqualTo(0.01m));

        var btcPerp = _repository.FindBySymbol("BTCUSDT", ProductType.PerpetualFuture, ExchangeEnum.BINANCE);
        Assert.That(btcPerp, Is.Not.Null);
        Assert.That(btcPerp.Symbol, Is.EqualTo("BTCUSDT"));
        Assert.That(btcPerp.ProductType, Is.EqualTo(ProductType.PerpetualFuture));
        Assert.That(btcPerp.TickSize, Is.EqualTo(0.1m));
    }

    [Test]
    public void LoadFromCsv_FileDoesNotExist_ThrowsFileNotFoundException()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "non_existent.csv");

        // Act & Assert
        Assert.Throws<FileNotFoundException>(() => _repository.LoadFromCsv(nonExistentPath));
    }

    [Test]
    public void LoadFromCsv_MalformedRecord_SkipsRecordAndLoadsValidOnes()
    {
        // Arrange
        var csvContent = @"market,symbol,type,base_currency,quote_currency,minimum_price_variation,lot_size,contract_multiplier,minimum_order_size
BINANCE,BTCUSDT,Spot,BTC,USDT,0.01,0.00001,1,10
BINANCE,ETHUSDT,Spot,ETH,USDT,invalid_decimal,0.0001,1,10"; // Malformed record
        var filePath = CreateTestCsvFile(csvContent);

        // Act
        _repository.LoadFromCsv(filePath);
        var allInstruments = _repository.GetAll().ToList();

        // Assert
        Assert.That(allInstruments.Count, Is.EqualTo(1)); // Only the valid record should be loaded
        Assert.That(allInstruments.First().Symbol, Is.EqualTo("BTCUSDT"));
    }

    [Test]
    public void FindBySymbol_ExistingInstrument_ReturnsCorrectInstrument()
    {
        // Arrange
        var csvContent = @"market,symbol,type,base_currency,quote_currency,minimum_price_variation,lot_size,contract_multiplier,minimum_order_size
BINANCE,BTCUSDT,PerpetualFuture,BTC,USDT,0.1,0.001,1,0.001";
        var filePath = CreateTestCsvFile(csvContent);
        _repository.LoadFromCsv(filePath);

        // Act
        var found = _repository.FindBySymbol("BTCUSDT", ProductType.PerpetualFuture, ExchangeEnum.BINANCE);

        // Assert
        Assert.That(found, Is.Not.Null);
        Assert.That(found.Symbol, Is.EqualTo("BTCUSDT"));
        Assert.That(found.ProductType, Is.EqualTo(ProductType.PerpetualFuture));
    }

    [Test]
    public void FindBySymbol_CaseInsensitive_ReturnsCorrectInstrument()
    {
        // Arrange
        var csvContent = @"market,symbol,type,base_currency,quote_currency,minimum_price_variation,lot_size,contract_multiplier,minimum_order_size
BINANCE,BTCUSDT,Spot,BTC,USDT,0.01,0.00001,1,10";
        var filePath = CreateTestCsvFile(csvContent);
        _repository.LoadFromCsv(filePath);

        // Act
        var found = _repository.FindBySymbol("btcusdt", ProductType.Spot, ExchangeEnum.BINANCE);

        // Assert
        Assert.That(found, Is.Not.Null);
        Assert.That(found.Symbol, Is.EqualTo("BTCUSDT"));
    }

    [Test]
    public void FindBySymbol_NonExistentInstrument_ReturnsNull()
    {
        // Arrange
        var csvContent = @"market,symbol,type,base_currency,quote_currency,minimum_price_variation,lot_size,contract_multiplier,minimum_order_size
BINANCE,BTCUSDT,Spot,BTC,USDT,0.01,0.00001,1,10";
        var filePath = CreateTestCsvFile(csvContent);
        _repository.LoadFromCsv(filePath);

        // Act
        var found = _repository.FindBySymbol("ETHUSDT", ProductType.Spot, ExchangeEnum.BINANCE);

        // Assert
        Assert.That(found, Is.Null);
    }

    [Test]
    public void GetById_ReturnsCorrectInstrument()
    {
        // Arrange
        var csvContent = @"market,symbol,type,base_currency,quote_currency,minimum_price_variation,lot_size,contract_multiplier,minimum_order_size
BINANCE,BTCUSDT,Spot,BTC,USDT,0.01,0.00001,1,10";
        var filePath = CreateTestCsvFile(csvContent);
        _repository.LoadFromCsv(filePath);
        var instrument = _repository.FindBySymbol("BTCUSDT", ProductType.Spot, ExchangeEnum.BINANCE);
        Assert.That(instrument, Is.Not.Null);

        // Act
        var foundById = _repository.GetById(instrument.InstrumentId);

        // Assert
        Assert.That(foundById, Is.SameAs(instrument));
    }
}