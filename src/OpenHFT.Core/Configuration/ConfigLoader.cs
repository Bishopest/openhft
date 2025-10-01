using System;
using System.Text.Json;

namespace OpenHFT.Core.Configuration;

public static class ConfigLoader
{
    public static SubscriptionConfig LoadSubscriptionConfig(string filePath = "config.json")
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Configuration file not found.", filePath);
        }

        var jsonString = File.ReadAllText(filePath);

        // This options object is not strictly necessary anymore because of [JsonPropertyName],
        // but it's good practice to keep for case-insensitivity of other properties.
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var config = JsonSerializer.Deserialize<SubscriptionConfig>(jsonString, options);

        if (config == null)
        {
            throw new InvalidDataException("Failed to deserialize config.json.");
        }

        return config;
    }
}