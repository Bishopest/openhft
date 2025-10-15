using NUnit.Framework;
using OpenHFT.Core.Models;

namespace OpenHFT.Tests.Core.Models;

[TestFixture]
public class PriceTests
{
    private const decimal PriceScale = 100_000_000m;

    [TestCase(123.4567, 12345670000)]
    [TestCase(-50.12, -5012000000)]
    [TestCase(0.0, 0)]
    [TestCase(987654321.9876, 98765432198760000)]
    public void FromDecimal_CreatesCorrectTicks(decimal input, long expectedTicks)
    {
        // Arrange & Act
        var price = Price.FromDecimal(input);

        // Assert
        Assert.That(expectedTicks, Is.EqualTo(price.ToTicks()));
        // Assert.That(input, Is.EqualTo(price.ToTicks())); // This was a bug in the original test
        Assert.That(input, Is.EqualTo(price.ToDecimal()));
    }

    [TestCase(12345670000, 123.4567)]
    [TestCase(-5012000000, -50.12)]
    [TestCase(0, 0.0)]
    [TestCase(98765432198760000, 987654321.9876)]
    public void FromTicks_CreatesCorrectDecimal(long inputTicks, decimal expectedDecimal)
    {
        // Arrange & Act
        var price = Price.FromTicks(inputTicks);

        // Assert
        Assert.That(expectedDecimal, Is.EqualTo(price.ToDecimal()));
        Assert.That(inputTicks, Is.EqualTo(price.ToTicks()));
    }

    [Test]
    public void Arithmetic_AdditionAndSubtraction_WorkCorrectly()
    {
        // Arrange
        var p1 = Price.FromDecimal(100.50m);
        var p2 = Price.FromDecimal(25.25m);

        // Act
        var sum = p1 + p2;
        var difference = p1 - p2;

        // Assert
        Assert.That(125.75m, Is.EqualTo(sum.ToDecimal()));
        Assert.That(75.25m, Is.EqualTo(difference.ToDecimal()));
    }

    [Test]
    public void Comparison_Operators_WorkCorrectly()
    {
        // Arrange
        var p100 = Price.FromDecimal(100m);
        var p200 = Price.FromDecimal(200m);
        var p100_copy = Price.FromDecimal(100m);

        // Assert
        Assert.That(p100 == p100_copy, Is.True);
        Assert.That(p100 != p200, Is.True);
        Assert.That(p200 > p100, Is.True);
        Assert.That(p100 < p200, Is.True);
        Assert.That(p100 >= p100_copy, Is.True);
        Assert.That(p100 <= p100_copy, Is.True);
        Assert.That(p100 > p200, Is.False);
    }

    [Test]
    public void Equals_Method_WorksCorrectly()
    {
        // Arrange
        var p1 = Price.FromDecimal(123.45m);
        var p2 = Price.FromDecimal(123.45m);
        var p3 = Price.FromDecimal(543.21m);

        // Assert
        Assert.That(p1.Equals(p2), Is.True);
        Assert.That(p1.Equals(p3), Is.False);
        Assert.That(p1.Equals(null), Is.False);
        Assert.That(p1.Equals(123.45m), Is.False); // Should not be equal to a different type
    }

    [Test]
    public void GetHashCode_IsConsistent()
    {
        // Arrange
        var p1 = Price.FromDecimal(123.45m);
        var p2 = Price.FromDecimal(123.45m);

        // Assert
        Assert.That(p1.GetHashCode(), Is.EqualTo(p2.GetHashCode()));
    }

    [Test]
    public void FromDecimal_WithExcessPrecision_TruncatesValue()
    {
        // Arrange: Input has more than 8 decimal places
        var preciseDecimal = 123.4567898765m;
        var expectedTruncatedDecimal = 123.45678987m;
        long expectedTicks = (long)(expectedTruncatedDecimal * PriceScale);

        // Act
        var price = Price.FromDecimal(preciseDecimal);

        // Assert
        Assert.That(expectedTicks, Is.EqualTo(price.ToTicks()));
        Assert.That(expectedTruncatedDecimal, Is.EqualTo(price.ToDecimal()));
    }
}
