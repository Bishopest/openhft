using System;
using System.Text.Json.Serialization;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Books;

public readonly struct BookInfo
{
    public string OmsIdentifier { get; }
    public string Name { get; }
    public bool Hedgeable { get; }

    [JsonConstructor]
    public BookInfo(string omsIdentifier, string name, bool hedgeable)
    {
        OmsIdentifier = omsIdentifier;
        Name = name;
        Hedgeable = hedgeable;
    }
}