using NUnit.Framework;
using Microsoft.Extensions.Logging.Abstractions;
using OpenHFT.Core.Configuration;
using Newtonsoft.Json;

namespace OpenHFT.Tests.Core.Configuration;

[TestFixture]
public class ConfigLoaderTests
{
    private string _testDirectory;

    [SetUp]
    public void SetUp()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "ConfigLoaderTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    private string CreateTestConfigFile(string content)
    {
        var filePath = Path.Combine(_testDirectory, "config.json");
        File.WriteAllText(filePath, content);
        return filePath;
    }

    [Test]
    public void Deserialize_ValidConfig_ParsesCorrectly()
    {
        // Arrange
        var jsonContent = @"
        {
          ""subscriptions"": [
            {
              ""exchange"": ""binance"",
              ""productType"": ""spot"",
              ""symbols"": [ ""BTCUSDT"" ]
            },
            {
              ""exchange"": ""binance"",
              ""productType"": ""perpetualfuture"",
              ""symbols"": [ ""BTCUSDT"", ""ETHUSDT"" ]
            }
          ]
        }";
        var configPath = CreateTestConfigFile(jsonContent);

        // Act
        var loader = new ConfigLoader(new NullLogger<ConfigLoader>(), configPath);
        var result = loader.Deserialize<SubscriptionConfig>();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Subscriptions, Is.Not.Null);
        Assert.That(result.Subscriptions.Count, Is.EqualTo(2), "Should parse two subscription groups.");

        var spotGroup = result.Subscriptions.FirstOrDefault(g => g.ProductType == "spot");
        Assert.That(spotGroup, Is.Not.Null);
        Assert.That(spotGroup.Exchange, Is.EqualTo("binance"));
        Assert.That(spotGroup.Symbols.Length, Is.EqualTo(1));
        Assert.That(spotGroup.Symbols[0], Is.EqualTo("BTCUSDT"));

        var perpGroup = result.Subscriptions.FirstOrDefault(g => g.ProductType == "perpetualfuture");
        Assert.That(perpGroup, Is.Not.Null);
        Assert.That(perpGroup.Exchange, Is.EqualTo("binance"));
        Assert.That(perpGroup.Symbols.Length, Is.EqualTo(2));
        Assert.That(perpGroup.Symbols, Contains.Item("BTCUSDT"));
        Assert.That(perpGroup.Symbols, Contains.Item("ETHUSDT"));
    }

    [Test]
    public void Constructor_FileDoesNotExist_ThrowsFileNotFoundException()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "non_existent_config.json");

        // Act & Assert
        Assert.Throws<FileNotFoundException>(() => new ConfigLoader(new NullLogger<ConfigLoader>(), nonExistentPath));
    }

    [Test]
    public void Constructor_MalformedJson_ThrowsJsonException()
    {
        // Arrange: Missing closing brace
        var malformedJsonContent = @"
        {
          ""subscriptions"": [
            {
              ""exchange"": ""binance"",
              ""product-type"": ""spot"",
              ""symbols"": [ ""BTCUSDT"" ]
            }";
        var configPath = CreateTestConfigFile(malformedJsonContent);

        // Act & Assert
        Assert.Throws<JsonReaderException>(() => new ConfigLoader(new NullLogger<ConfigLoader>(), configPath));
    }

    [Test]
    public void Deserialize_EmptySubscriptionsArray_ReturnsEmptyList()
    {
        // Arrange
        var jsonContent = @"{ ""subscriptions"": [] }";
        var configPath = CreateTestConfigFile(jsonContent);

        // Act
        var loader = new ConfigLoader(new NullLogger<ConfigLoader>(), configPath);
        var result = loader.Deserialize<SubscriptionConfig>();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Subscriptions, Is.Not.Null);
        Assert.That(result.Subscriptions, Is.Empty, "Subscriptions list should be empty.");
    }

    [Test]
    public void Deserialize_MissingSubscriptionsKey_StillDeserializesButListIsEmpty()
    {
        // Arrange: The root "subscriptions" key is missing
        var jsonContent = @"{ ""some_other_key"": [] }";
        var configPath = CreateTestConfigFile(jsonContent);

        // Act
        var loader = new ConfigLoader(new NullLogger<ConfigLoader>(), configPath);
        var result = loader.Deserialize<SubscriptionConfig>();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Subscriptions, Is.Not.Null);
        Assert.That(result.Subscriptions, Is.Empty, "If 'subscriptions' key is missing, the list should be empty by default.");
    }

    [Test]
    public void Get_StringValue_ReturnsCorrectValue()
    {
        // Arrange
        var jsonContent = @"{ ""dataFolder"": ""/var/data"", ""subscriptions"": [] }";
        var configPath = CreateTestConfigFile(jsonContent);
        var loader = new ConfigLoader(new NullLogger<ConfigLoader>(), configPath);

        // Act
        var result = loader.Get("dataFolder");

        // Assert
        Assert.That(result, Is.EqualTo("/var/data"));
    }

    [Test]
    public void Get_KeyNotFound_ReturnsDefaultValue()
    {
        // Arrange
        var jsonContent = @"{ ""subscriptions"": [] }";
        var configPath = CreateTestConfigFile(jsonContent);
        var loader = new ConfigLoader(new NullLogger<ConfigLoader>(), configPath);

        // Act
        var result = loader.Get("non-existent-key", "default-value");

        // Assert
        Assert.That(result, Is.EqualTo("default-value"));
    }

    [Test]
    public void GetValue_NestedKey_ReturnsCorrectValue()
    {
        // Arrange
        var jsonContent = @"{ ""settings"": { ""network"": { ""timeout"": 30 } } }";
        var configPath = CreateTestConfigFile(jsonContent);
        var loader = new ConfigLoader(new NullLogger<ConfigLoader>(), configPath);

        // Act
        var timeout = loader.GetInt("settings:network:timeout");

        // Assert
        Assert.That(timeout, Is.EqualTo(30));
    }

    [Test]
    public void GetValue_DifferentTypes_ReturnsCorrectTypes()
    {
        // Arrange
        var jsonContent = @"{ ""intValue"": 123, ""boolValue"": true, ""doubleValue"": 45.67 }";
        var configPath = CreateTestConfigFile(jsonContent);
        var loader = new ConfigLoader(new NullLogger<ConfigLoader>(), configPath);

        // Assert
        Assert.That(loader.GetInt("intValue"), Is.EqualTo(123));
        Assert.That(loader.GetBool("boolValue"), Is.True);
        Assert.That(loader.GetDouble("doubleValue"), Is.EqualTo(45.67));
    }
}