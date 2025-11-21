using System;
using Newtonsoft.Json;

namespace OpenHFT.Core.Configuration;

public class BookConfig
{
    [JsonProperty("bookName")]
    public string BookName { get; set; } = string.Empty;

    [JsonProperty("hedgeable")]
    public bool Hedgeable { get; set; }
}