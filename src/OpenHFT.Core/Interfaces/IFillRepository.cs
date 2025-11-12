using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Interfaces;

public interface IFillRepository
{
    /// <summary>
    /// Adds a new fill record to the repository.
    /// </summary>
    void AddFill(Fill fill);

    /// <summary>
    /// Retrieves all fills for a specific date.
    /// </summary>
    IEnumerable<Fill> GetFillsByDate(DateTime date);

    /// <summary>
    /// Retrieves fills within a specific date range.
    /// </summary>
    IEnumerable<Fill> GetFillsByDateRange(DateTime startDate, DateTime endDate);

    IEnumerable<Fill> GetFillsByInstrument(int instrumentId, DateTime date);
}