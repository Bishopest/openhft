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
    [Key] // InstrumentId is the natural primary key for this table.
    public int InstrumentId { get; set; }

    public string BookName { get; set; } = string.Empty;
    public decimal AvgPrice { get; set; }
    public decimal Size { get; set; }
    public decimal RealizedPnLAmount { get; set; }
    public string RealizedPnLCurrency { get; set; } = string.Empty;
    public decimal VolumeInUsdtAmount { get; set; }
    public string VolumeInUsdtCurrency { get; set; } = string.Empty;
    public long LastUpdateTime { get; set; }

    public static BookElementDbo FromBookElement(BookElement element)
    {
        return new BookElementDbo
        {
            InstrumentId = element.InstrumentId,
            BookName = element.BookName,
            AvgPrice = element.AvgPrice.ToDecimal(),
            Size = element.Size.ToDecimal(),
            RealizedPnLAmount = element.RealizedPnL.Amount,
            RealizedPnLCurrency = element.RealizedPnL.Currency.Symbol,
            VolumeInUsdtAmount = element.VolumeInUsdt.Amount,
            VolumeInUsdtCurrency = element.VolumeInUsdt.Currency.Symbol,
            LastUpdateTime = element.LastUpdateTime
        };
    }

    public BookElement ToBookElement()
    {
        return new BookElement(
            BookName,
            InstrumentId,
            Price.FromDecimal(AvgPrice),
            Quantity.FromDecimal(Size),
            CurrencyAmount.FromDecimal(RealizedPnLAmount, Currency.FromString(RealizedPnLCurrency)),
            CurrencyAmount.FromDecimal(VolumeInUsdtAmount, Currency.FromString(VolumeInUsdtCurrency)),
            LastUpdateTime
        );
    }
}