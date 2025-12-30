using System;
using OpenHFT.Core.Books;

namespace OpenHFT.GUI.Services;

public interface IBookCacheService
{
    event Action? OnBookUpdated;

    /// <summary>
    /// Gets book names associated with a specific OMS identifier.
    /// If omsIdentifier is null or empty, returns all known book names.
    /// </summary>
    IEnumerable<string> GetBookNames(string? omsIdentifier = null);
    /// <summary>
    /// Gets oms identifier associated with a specific book name supposing book name is unique across all oms identifiers.
    /// </summary>
    string? GetOmsIdentifierByBookName(string bookName);
    /// <summary>
    /// Gets all elements for a specific book name, aggregated from all OMS servers.
    /// </summary>
    IEnumerable<BookElement> GetElementsByBookName(string bookName);

    /// <summary>
    /// Clears all data for a specific OMS.
    /// </summary>
    void ClearCacheForOms(string omsIdentifier);
}
