using System;
using OpenHFT.Core.Books;

namespace OpenHFT.Core.Interfaces;

public interface IBookManager
{
    /// <summary>
    /// Fired whenever a BookElement's state is updated.
    /// </summary>
    event EventHandler<BookElement> BookElementUpdated;
    BookElement GetBookElement(string bookName, int instrumentId);
    IReadOnlyCollection<BookElement> GetAllBookElements();
    IReadOnlyCollection<BookInfo> GetAllBookInfos();
    IReadOnlyCollection<BookElement> GetElementsByBookName(string bookName);
}
