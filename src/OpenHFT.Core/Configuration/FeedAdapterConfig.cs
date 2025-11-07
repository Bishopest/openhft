using System;
using System.Text.Json.Serialization;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Configuration;

public class FeedAdapterConfig
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ExchangeEnum Exchange { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProductType ProductType { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ExecutionMode ExecutionMode { get; set; }
}
