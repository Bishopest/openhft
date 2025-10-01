using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OpenHFT.Core.Configuration;

public class ConfigLoader
{
    private readonly ILogger<ConfigLoader> _logger;
    private readonly JObject _settings;
    private readonly string _filePath;

    public ConfigLoader(ILogger<ConfigLoader> logger, string filePath = "config.json")
    {
        _logger = logger;
        _filePath = filePath;

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Configuration file not found.", filePath);
        }

        var jsonString = File.ReadAllText(filePath);
        _settings = JObject.Parse(jsonString);
    }

    /// <summary>
    /// Deserializes the entire configuration into a specific type.
    /// </summary>
    public T Deserialize<T>()
    {
        return _settings.ToObject<T>()
               ?? throw new InvalidDataException($"Failed to deserialize config into {typeof(T).Name}.");
    }

    /// <summary>
    /// Get the matching config setting from the file searching for this key.
    /// </summary>
    /// <returns>String value of the configuration setting or default value if nothing found.</returns>
    public string Get(string key, string defaultValue = "")
    {
        return GetValue(key, defaultValue);
    }

    /// <summary>
    /// Get a boolean value configuration setting by a configuration key.
    /// </summary>
    /// <returns>Boolean value of the config setting.</returns>
    public bool GetBool(string key, bool defaultValue = false)
    {
        return GetValue(key, defaultValue);
    }

    /// <summary>
    /// Get the int value of a config string.
    /// </summary>
    /// <returns>Int value of the config setting.</returns>
    public int GetInt(string key, int defaultValue = 0)
    {
        return GetValue(key, defaultValue);
    }

    /// <summary>
    /// Get the double value of a config string.
    /// </summary>
    /// <returns>Double value of the config setting.</returns>
    public double GetDouble(string key, double defaultValue = 0.0)
    {
        return GetValue(key, defaultValue);
    }

    /// <summary>
    /// Gets a value from configuration and converts it to the requested type.
    /// </summary>
    public T GetValue<T>(string key, T defaultValue = default)
    {
        var token = GetToken(_settings, key);
        if (token == null)
        {
            _logger.LogTrace("Config.GetValue(): Configuration key not found. Key: {Key} - Using default value: {DefaultValue}", key, defaultValue);
            return defaultValue;
        }

        try
        {
            // For simple values (string, int, bool, etc.), this works directly.
            if (token is JValue jValue)
            {
                if (jValue.ToObject<T>() is T value)
                {
                    return value;
                }
            }

            // For complex objects, deserialize the node.
            return token.ToObject<T>() ?? defaultValue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Config.GetValue<{Type}>({Key}): Failed to parse or convert value. Using default value.", typeof(T).Name, key);
            return defaultValue;
        }
    }

    /// <summary>
    /// Sets a configuration value in memory.
    /// </summary>
    public void Set(string key, object value)
    {
        var path = key.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
        JToken currentNode = _settings;

        for (int i = 0; i < path.Length - 1; i++)
        {
            var segment = path[i];
            if (currentNode[segment] is not JObject nextNode)
            {
                nextNode = new JObject();
                currentNode[segment] = nextNode;
            }
            currentNode = nextNode;
        }

        currentNode[path.Last()] = JToken.FromObject(value);
    }

    /// <summary>
    /// Write the contents of the in-memory configuration back to the disk.
    /// </summary>
    public void Write(string? targetPath = null)
    {
        var serialized = _settings.ToString(Formatting.Indented);

        var finalPath = string.IsNullOrEmpty(targetPath) ? _filePath : targetPath;
        File.WriteAllText(finalPath, serialized);
    }

    private JToken? GetToken(JToken settings, string key)
    {
        // Newtonsoft.Json's SelectToken uses dot-notation for JSONPath.
        // We'll replace our colon separator with dots to leverage this.
        var jsonPath = key.Replace(':', '.');
        return settings.SelectToken(jsonPath);
    }
}