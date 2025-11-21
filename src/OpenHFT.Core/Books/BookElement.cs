using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Books;

public readonly struct BookElement : IEquatable<BookElement>
{
    public readonly string BookName;
    public readonly int InstrumentId;
    public readonly Price AvgPrice;
    // + : buy, - : sell
    public readonly Quantity Size;
    public readonly CurrencyAmount RealizedPnL;
    public readonly CurrencyAmount VolumeInUsdt;
    public readonly long LastUpdateTime;

    public BookElement(string bookName,
                       int instrumentId,
                       Price avgPrice,
                       Quantity size,
                       CurrencyAmount realizedPnL,
                       CurrencyAmount volumeInUsdt,
                       long lastUpdateTime)
    {
        BookName = bookName;
        InstrumentId = instrumentId;
        AvgPrice = avgPrice;
        Size = size;
        RealizedPnL = realizedPnL;
        VolumeInUsdt = volumeInUsdt;
        LastUpdateTime = lastUpdateTime;
    }

    public override bool Equals(object? obj) => obj is BookElement other && Equals(other);

    public bool Equals(BookElement other)
    {
        // BookName, InstrumentId, Quantity 등 모든 주요 필드를 비교
        return BookName == other.BookName &&
               InstrumentId == other.InstrumentId &&
               AvgPrice.Equals(other.AvgPrice) &&
               Size.Equals(other.Size) &&
               RealizedPnL.Equals(other.RealizedPnL) &&
               VolumeInUsdt.Equals(other.VolumeInUsdt);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(BookName);
        hash.Add(InstrumentId);
        hash.Add(AvgPrice);
        hash.Add(Size);
        hash.Add(RealizedPnL);
        hash.Add(VolumeInUsdt);
        return hash.ToHashCode();
    }

    public static bool operator ==(BookElement left, BookElement right) => left.Equals(right);
    public static bool operator !=(BookElement left, BookElement right) => !left.Equals(right);
}