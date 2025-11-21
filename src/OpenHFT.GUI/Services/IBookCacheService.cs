using System;
using OpenHFT.Core.Books;

namespace OpenHFT.GUI.Services;

public interface IBookCacheService
{
    event Action? OnBookUpdated;

    /// <summary>
    /// Gets all known book names across all connected OMS servers.
    /// </summary>
    IEnumerable<string> GetBookNames();

    /// <summary>
    /// Gets all elements for a specific book name, aggregated from all OMS servers.
    /// </summary>
    IEnumerable<BookElement> GetElementsByBookName(string bookName);

    /// <summary>
    /// Clears all data for a specific OMS.
    /// </summary>
    void ClearCacheForOms(string omsIdentifier);

}
