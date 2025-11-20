using System;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Books;

public class Book
{
    public string Name { get; }
    public IReadOnlySet<int> InstrumentIds { get; }

    public Book(string name, IEnumerable<int> instrumentIds)
    {
        Name = name;
        InstrumentIds = new HashSet<int>(instrumentIds);
    }
}