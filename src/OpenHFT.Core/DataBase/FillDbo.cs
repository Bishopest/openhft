using System;
using System.ComponentModel.DataAnnotations;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.DataBase;

// This class is specifically for storing Fill data in the database.
public class FillDbo
{
    [Key] // Explicitly mark this as the primary key
    public long FillDboId { get; set; }

    public int InstrumentId { get; set; }
    public string BookName { get; set; } = string.Empty;
    public long ClientOrderId { get; set; }
    public string ExchangeOrderId { get; set; } = string.Empty;
    public string ExecutionId { get; set; } = string.Empty;
    public Side Side { get; set; }
    public decimal FilledPrice { get; set; } // Store as decimal in DB
    public decimal FilledQuantity { get; set; } // Store as decimal in DB
    public long Timestamp { get; set; }

    // Parameterless constructor for EF Core
    public FillDbo() { }

    // A helper method to create a DBO from a Fill struct
    public static FillDbo FromFill(Fill fill)
    {
        return new FillDbo
        {
            InstrumentId = fill.InstrumentId,
            BookName = fill.BookName,
            ClientOrderId = fill.ClientOrderId,
            ExchangeOrderId = fill.ExchangeOrderId,
            ExecutionId = fill.ExecutionId,
            Side = fill.Side,
            FilledPrice = fill.Price.ToDecimal(),
            FilledQuantity = fill.Quantity.ToDecimal(),
            Timestamp = fill.Timestamp
        };
    }

    // A helper method to convert a DBO back to a Fill struct
    public Fill ToFill()
    {
        return new Fill(
            InstrumentId,
            BookName,
            ClientOrderId,
            ExchangeOrderId,
            ExecutionId,
            Side,
            Price.FromDecimal(FilledPrice),
            Quantity.FromDecimal(FilledQuantity),
            Timestamp
        );
    }
}