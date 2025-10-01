using System.Text.Json;
using NUnit.Framework;
using OpenHFT.Core.Configuration;

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
    public void LoadFromFile_ValidConfig_ParsesCorrectly()
    {
        // Arrange
        var jsonContent = @"
        {
          ""subscriptions"": [
            {
              ""exchange"": ""binance"",
              ""product-type"": ""spot"",
              ""symbols"": [ ""BTCUSDT"" ]
            },
            {
              ""exchange"": ""binance"",
              ""product-type"": ""perpetual"",
              ""symbols"": [ ""BTCUSDT"", ""ETHUSDT"" ]
            }
          ]
        }";
        var configPath = CreateTestConfigFile(jsonContent);

        // Act
        var result = ConfigLoader.LoadSubscriptionConfig(configPath);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Subscriptions, Is.Not.Null);
        Assert.That(result.Subscriptions.Count, Is.EqualTo(2), "Should parse two subscription groups.");

        var spotGroup = result.Subscriptions.FirstOrDefault(g => g.ProductType == "spot");
        Assert.That(spotGroup, Is.Not.Null);
        Assert.That(spotGroup.Exchange, Is.EqualTo("binance"));
        Assert.That(spotGroup.Symbols.Length, Is.EqualTo(1));
        Assert.That(spotGroup.Symbols[0], Is.EqualTo("BTCUSDT"));

        var perpGroup = result.Subscriptions.FirstOrDefault(g => g.ProductType == "perpetual");
        Assert.That(perpGroup, Is.Not.Null);
        Assert.That(perpGroup.Exchange, Is.EqualTo("binance"));
        Assert.That(perpGroup.Symbols.Length, Is.EqualTo(2));
        Assert.That(perpGroup.Symbols, Contains.Item("BTCUSDT"));
        Assert.That(perpGroup.Symbols, Contains.Item("ETHUSDT"));
    }

    [Test]
    public void LoadFromFile_FileDoesNotExist_ThrowsFileNotFoundException()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "non_existent_config.json");

        // Act & Assert
        Assert.Throws<FileNotFoundException>(() => ConfigLoader.LoadSubscriptionConfig(nonExistentPath));
    }

    [Test]
    public void LoadFromFile_MalformedJson_ThrowsJsonException()
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
        Assert.Throws<JsonException>(() => ConfigLoader.LoadSubscriptionConfig(configPath));
    }

    [Test]
    public void LoadFromFile_EmptySubscriptionsArray_ReturnsEmptyList()
    {
        // Arrange
        var jsonContent = @"{ ""subscriptions"": [] }";
        var configPath = CreateTestConfigFile(jsonContent);

        // Act
        var result = ConfigLoader.LoadSubscriptionConfig(configPath);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Subscriptions, Is.Not.Null);
        Assert.That(result.Subscriptions, Is.Empty, "Subscriptions list should be empty.");
    }

    [Test]
    public void LoadFromFile_MissingSubscriptionsKey_StillDeserializesButListIsEmpty()
    {
        // Arrange: The root "subscriptions" key is missing
        var jsonContent = @"{ ""some_other_key"": [] }";
        var configPath = CreateTestConfigFile(jsonContent);

        // Act
        var result = ConfigLoader.LoadSubscriptionConfig(configPath);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Subscriptions, Is.Not.Null);
        Assert.That(result.Subscriptions, Is.Empty, "If 'subscriptions' key is missing, the list should be empty by default.");
    }
}