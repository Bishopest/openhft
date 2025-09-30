using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Interfaces;

public interface IInstrumentRepository
{
    /// <summary>
    /// Loads instrument definitions from a specified CSV file path.
    /// This should be called once at application startup.
    /// </summary>
    /// <param name="filePath">The path to the symbol properties CSV file.</param>
    void LoadFromCsv(string filePath);

    /// <summary>
    /// Retrieves an instrument by its unique internal ID.
    /// </summary>
    /// <param name="instrumentId">The unique ID of the instrument.</param>
    /// <returns>The Instrument object, or null if not found.</returns>
    Instrument? GetById(int instrumentId);

    /// <summary>
    /// Finds an instrument using its market symbol and product type.
    /// This is useful for mapping incoming feed data to an internal instrument object.
    /// </summary>
    /// <param name="symbol">The market-specific symbol (e.g., "BTCUSDT").</param>
    /// <param name="productType">The product type (e.g., Spot, PerpetualFuture).</param>
    /// <param name="exchange">The exchange where the symbol is traded.</param>
    /// <returns>The Instrument object, or null if not found.</returns>
    Instrument? FindBySymbol(string symbol, ProductType productType, ExchangeEnum exchange);

    /// <summary>
    /// Gets all loaded instruments.
    /// </summary>
    /// <returns>A collection of all instruments.</returns>
    IEnumerable<Instrument> GetAll();
}
