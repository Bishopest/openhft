using System;
using OpenHFT.Core.Books;

namespace OpenHFT.Core.Interfaces;

public interface IBookRepository
{
    Task SaveElementAsync(BookElement element);
    Task<IEnumerable<BookElement>> LoadAllElementsAsync();
}
