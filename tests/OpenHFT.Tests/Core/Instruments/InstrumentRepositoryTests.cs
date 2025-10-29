using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;

namespace OpenHFT.Tests.Core.Instruments;

[TestFixture]
public class InstrumentRepositoryTests
{
    private ServiceProvider _serviceProvider = null!;
    private string _testDirectory;
    private InstrumentRepository _repository;

    private void SetupRepositoryWithContent(string csvContent)
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "InstrumentRepositoryTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);

        var filePath = Path.Combine(_testDirectory, "instruments.csv");
        File.WriteAllText(filePath, csvContent);

        var inMemorySettings = new Dictionary<string, string>
        {
            { "dataFolder", _testDirectory }
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var services = new ServiceCollection();

        services.AddLogging(builder => builder.AddConsole()); // AddConsole() 사용
        services.AddSingleton(configuration);
        services.AddSingleton<IInstrumentRepository, InstrumentRepository>();

        _serviceProvider = services.BuildServiceProvider();

        // Get the repository instance. The constructor will automatically load the CSV.
        _repository = (InstrumentRepository)_serviceProvider.GetRequiredService<IInstrumentRepository>();

    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [Test]
    public void LoadFromCsv_ValidFile_LoadsInstrumentsCorrectly()
    {
        // Arrange
        var csvContent = @"market,symbol,type,base_currency,quote_currency,minimum_price_variation,lot_size,contract_multiplier,minimum_order_size
BINANCE,BTCUSDT,Spot,BTC,USDT,0.01,0.00001,1,10
BINANCE,ETHUSDT,Spot,ETH,USDT,0.01,0.0001,1,10
BINANCE,BTCUSDT,PerpetualFuture,BTC,USDT,0.1,0.001,1,0.001";
        SetupRepositoryWithContent(csvContent);

        // Act
        var allInstruments = _repository.GetAll().ToList();
        allInstruments.Should().HaveCount(3);

        var btcSpot = _repository.FindBySymbol("BTCUSDT", ProductType.Spot, ExchangeEnum.BINANCE);
        Assert.That(btcSpot, Is.Not.Null);
        Assert.That(btcSpot.Symbol, Is.EqualTo("BTCUSDT"));
        Assert.That(btcSpot.ProductType, Is.EqualTo(ProductType.Spot));
        Assert.That(btcSpot.TickSize.ToDecimal(), Is.EqualTo(0.01m));

        var btcPerp = _repository.FindBySymbol("BTCUSDT", ProductType.PerpetualFuture, ExchangeEnum.BINANCE);
        Assert.That(btcPerp, Is.Not.Null);
        Assert.That(btcPerp.Symbol, Is.EqualTo("BTCUSDT"));
        Assert.That(btcPerp.ProductType, Is.EqualTo(ProductType.PerpetualFuture));
        Assert.That(btcPerp.TickSize.ToDecimal(), Is.EqualTo(0.1m));
    }

    [Test]
    public void LoadFromCsv_FileDoesNotExist_ThrowsFileNotFoundException()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "InstrumentRepositoryTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "non_existent.csv");
        var inMemorySettings = new Dictionary<string, string>
        {
            { "dataFolder", nonExistentPath }
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton(configuration);
        services.AddSingleton<IInstrumentRepository, InstrumentRepository>();

        // Act & Assert
        var serviceProvider = services.BuildServiceProvider();

        var ex = Assert.Throws<FileNotFoundException>(() =>
            serviceProvider.GetRequiredService<IInstrumentRepository>()
        );
        _serviceProvider = serviceProvider;
    }

    [Test]
    public void LoadFromCsv_MalformedRecord_SkipsRecordAndLoadsValidOnes()
    {
        // Arrange
        var csvContent = @"market,symbol,type,base_currency,quote_currency,minimum_price_variation,lot_size,contract_multiplier,minimum_order_size
BINANCE,BTCUSDT,Spot,BTC,USDT,0.01,0.00001,1,10
BINANCE,ETHUSDT,Spot,ETH,USDT,invalid_decimal,0.0001,1,10"; // Malformed record
        SetupRepositoryWithContent(csvContent);

        // Act
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
        SetupRepositoryWithContent(csvContent);
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
        SetupRepositoryWithContent(csvContent);

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
        SetupRepositoryWithContent(csvContent);

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
        SetupRepositoryWithContent(csvContent);

        var instrument = _repository.FindBySymbol("BTCUSDT", ProductType.Spot, ExchangeEnum.BINANCE);
        Assert.That(instrument, Is.Not.Null);

        // Act
        var foundById = _repository.GetById(instrument.InstrumentId);

        // Assert
        Assert.That(foundById, Is.SameAs(instrument));
    }
}