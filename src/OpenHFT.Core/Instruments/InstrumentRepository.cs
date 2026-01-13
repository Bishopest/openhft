using System;
using System.Globalization;
using CsvHelper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;

namespace OpenHFT.Core.Instruments;

public class InstrumentRepository : IInstrumentRepository
{
    private readonly ILogger<InstrumentRepository> _logger;
    private readonly IConfiguration _configuration;

    // Main storage for fast lookup by our internal ID
    private readonly Dictionary<int, Instrument> _instrumentsById = new();

    // Secondary index for fast lookup by symbol, type, and exchange
    private readonly Dictionary<(string, ProductType, ExchangeEnum), Instrument> _instrumentsByDetails = new();

    public InstrumentRepository(ILogger<InstrumentRepository> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _ = StartAsync();
    }
    public Task StartAsync()
    {
        _logger.LogInformationWithCaller("Loading instruments from CSV specified in configuration...");
        var dataFolder = _configuration["dataFolder"];

        if (string.IsNullOrEmpty(dataFolder))
        {
            throw new InvalidOperationException("Configuration key 'dataFolder' is not set in config.json.");
        }

        var instrumentsCsvPath = Path.Combine(dataFolder, "instruments.csv");
        LoadFromCsv(instrumentsCsvPath);
        _logger.LogInformationWithCaller($"Instruments loaded successfully from '{instrumentsCsvPath}'.");

        return Task.CompletedTask;
    }

    public void LoadFromCsv(string filePath)
    {
        if (!File.Exists(filePath))
        {
            var ex = new FileNotFoundException("Instrument definition file not found.", filePath);
            _logger.LogErrorWithCaller(ex, $"Instrument definition file not found at {filePath}");
            throw ex;
        }

        using var reader = new StreamReader(filePath);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

        // This assumes your CSV header names match the property names in this anonymous type.
        // If not, you'll need to create a mapping class.
        var records = csv.GetRecords<dynamic>();

        foreach (var record in records)
        {
            try
            {
                string idString = (string)record.instrument_id;
                if (!int.TryParse(idString, out var instrumentId))
                {
                    _logger.LogWarningWithCaller($"Invalid or missing instrument_id in CSV record. Skipping record.");
                    continue;
                }

                var market = Enum.Parse<ExchangeEnum>(record.market, true);
                var symbol = (string)record.symbol;
                Enum.TryParse<ProductType>(record.type, true, out ProductType productType);
                var baseCurrency = Currency.FromString(record.base_currency); // Simplified logic
                var quoteCurrency = Currency.FromString(record.quote_currency);
                var tickSize = Price.FromDecimal(decimal.Parse(record.minimum_price_variation));
                var lotSize = Quantity.FromDecimal(decimal.Parse(record.lot_size));
                var multiplier = decimal.Parse(record.contract_multiplier);

                Quantity minOrderSize;
                try
                {
                    minOrderSize = Quantity.FromDecimal(decimal.Parse(record.minimum_order_size));
                }
                catch (Exception)
                {
                    minOrderSize = lotSize;
                }

                Instrument newInstrument;

                switch (productType)
                {
                    case ProductType.Spot:
                        newInstrument = new Crypto(instrumentId, symbol, market, baseCurrency, quoteCurrency, tickSize, lotSize, minOrderSize);
                        break;
                    case ProductType.PerpetualFuture: // Assuming 'cryptofuture' in CSV means Perpetual
                        newInstrument = new CryptoPerpetual(instrumentId, symbol, market, baseCurrency, quoteCurrency, tickSize, lotSize, multiplier, minOrderSize);
                        break;
                    // Add cases for DatedFuture, Option etc. as needed
                    default:
                        _logger.LogWarningWithCaller($"Unsupported product type '{record.type}' for symbol {symbol}. Skipping.");
                        continue;
                }

                if (_instrumentsById.ContainsKey(newInstrument.InstrumentId))
                {
                    _logger.LogWarningWithCaller($"Duplicate InstrumentId '{newInstrument.InstrumentId}' found in CSV for symbol {newInstrument.Symbol}. Skipping.");
                    continue;
                }

                _instrumentsById[newInstrument.InstrumentId] = newInstrument;
                _instrumentsByDetails[(newInstrument.Symbol.ToUpperInvariant(), newInstrument.ProductType, newInstrument.SourceExchange)] = newInstrument;
            }
            catch (Exception ex)
            {
                _logger.LogErrorWithCaller(ex, $"Failed to parse instrument record: {record}");
            }
        }

        _logger.LogInformationWithCaller($"Successfully loaded {_instrumentsById.Count} instruments from {filePath}.");
    }

    public Instrument? GetById(int instrumentId)
    {
        _instrumentsById.TryGetValue(instrumentId, out var instrument);
        return instrument;
    }

    public Instrument? FindBySymbol(string symbol, ProductType productType, ExchangeEnum exchange)
    {
        _instrumentsByDetails.TryGetValue((symbol.ToUpperInvariant(), productType, exchange), out var instrument);
        return instrument;
    }

    public IEnumerable<Instrument> GetAll() => _instrumentsById.Values;
}