using System;
using System.ComponentModel.DataAnnotations;
using OpenHFT.Core.Books;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.DataBase;

/// <summary>
/// Database representation (Data Transfer Object) for the BookElement struct.
/// </summary>
public class BookElementDbo
{
    public int InstrumentId { get; set; }
    public string BookName { get; set; } = string.Empty;
    public decimal AvgPriceAccum { get; set; }
    public decimal SizeAccum { get; set; }
    public decimal RealizedPnLAccum { get; set; }
    public string RealizedPnLCurrency { get; set; } = string.Empty;
    public decimal VolumeAccum { get; set; }
    public string VolumeCurrency { get; set; } = string.Empty;
    public long LastUpdateTime { get; set; }

    /// <summary>
    /// Creates a DTO from a BookElement, saving only the cumulative data.
    /// </summary>
    public static BookElementDbo FromBookElement(BookElement element)
    {
        return new BookElementDbo
        {
            InstrumentId = element.InstrumentId,
            BookName = element.BookName,
            AvgPriceAccum = element.AvgPriceAccum.ToDecimal(),
            SizeAccum = element.SizeAccum.ToDecimal(),
            RealizedPnLAccum = element.RealizedPnLAccum.Amount,
            RealizedPnLCurrency = element.RealizedPnLAccum.Currency.Symbol,
            VolumeAccum = element.VolumeAccum.Amount,
            VolumeCurrency = element.VolumeAccum.Currency.Symbol,
            LastUpdateTime = element.LastUpdateTime
        };
    }

    public BookElement ToBookElement()
    {
        // This is equivalent to calling the CreateWithBasePosition factory method.
        return new BookElement(
            bookName: BookName,
            instrumentId: InstrumentId,
            lastUpdateTime: LastUpdateTime,

            // Session fields are initialized to zero
            avgPrice: Price.Zero,
            size: Quantity.Zero,
            realizedPnL: CurrencyAmount.Zero(Currency.USDT),
            volume: CurrencyAmount.Zero(Currency.USDT),

            // Cumulative fields are restored from the database
            avgPriceAccum: Price.FromDecimal(AvgPriceAccum),
            sizeAccum: Quantity.FromDecimal(SizeAccum),
            realizedPnLAccum: CurrencyAmount.FromDecimal(RealizedPnLAccum, Currency.FromString(RealizedPnLCurrency)),
            volumeAccum: CurrencyAmount.FromDecimal(VolumeAccum, Currency.FromString(VolumeCurrency))
        );
    }
}