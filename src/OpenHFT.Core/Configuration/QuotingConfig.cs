using System;
using System.Text.Json.Serialization;
using Newtonsoft.Json;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Configuration;

/// <summary>
/// Represents the parameters for the fair value calculation model.
/// Maps to the "fairValue.params" section in the config.json.
/// </summary>
public class FairValueParams
{
    [JsonProperty("exchange")]
    public string Exchange { get; set; } = string.Empty;

    [JsonProperty("productType")]
    public string ProductType { get; set; } = string.Empty;

    [JsonProperty("symbol")]
    public string Symbol { get; set; } = string.Empty;
}

/// <summary>
/// Represents the configuration for the fair value model.
/// Maps to the "fairValue" section in the config.json.
/// </summary>
public class FairValueConfig
{
    // The JSON deserializer can automatically map the string "Midp" or "FR"
    // to the FairValueModel enum values.
    [JsonProperty("model")]
    public FairValueModel Model { get; set; }

    [JsonProperty("params")]
    public FairValueParams Params { get; set; } = new();
}

/// <summary>
/// Represents the entire configuration for a single quoting strategy instance.
/// Maps to the root "quoting" object in the config.json.
/// </summary>
public class QuotingConfig
{
    [JsonProperty("exchange")]
    public string Exchange { get; set; } = string.Empty;

    [JsonProperty("productType")]
    public string ProductType { get; set; } = string.Empty;

    [JsonProperty("quoterType")]
    public string QuoterType { get; set; } = string.Empty;

    [JsonProperty("symbol")]
    public string Symbol { get; set; } = string.Empty;

    [JsonProperty("fairValue")]
    public FairValueConfig FairValue { get; set; } = new();

    [JsonProperty("askSpreadBp")]
    public decimal AskSpreadBp { get; set; }

    [JsonProperty("bidSpreadBp")]
    public decimal BidSpreadBp { get; set; }

    [JsonProperty("skewBp")]
    public decimal SkewBp { get; set; }

    [JsonProperty("size")]
    public decimal Size { get; set; }

    [JsonProperty("depth")]
    public int Depth { get; set; }
    [JsonProperty("postOnly")]
    public bool PostOnly { get; set; }
}