using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Interfaces;

public interface IFillRepository
{
    /// <summary>
    /// Adds a new fill record to the repository.
    /// </summary>
    Task AddFillAsync(Fill fill);

    /// <summary>
    /// Retrieves all fills for a specific date.
    /// </summary>
    Task<IEnumerable<Fill>> GetFillsByDateAsync(DateTime date);

    /// <summary>
    /// Retrieves fills within a specific date range.
    /// </summary>
    Task<IEnumerable<Fill>> GetFillsByDateRangeAsync(DateTime startDate, DateTime endDate);

    Task<IEnumerable<Fill>> GetFillsByInstrumentAsync(int instrumentId, DateTime date);
}