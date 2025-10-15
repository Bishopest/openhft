using NUnit.Framework;
using OpenHFT.Core.Models;

namespace OpenHFT.Tests.Core.Models;

[TestFixture]
public class QuantityTests
{
    private const decimal QuantityScale = 100_000_000m;

    [TestCase(1.23456789, 123456789)]
    [TestCase(-0.5, -50000000)]
    [TestCase(0.0, 0)]
    [TestCase(500.0, 50000000000)]
    public void FromDecimal_CreatesCorrectTicks(decimal input, long expectedTicks)
    {
        // Arrange & Act
        var quantity = Quantity.FromDecimal(input);

        // Assert
        Assert.That(expectedTicks, Is.EqualTo(quantity.ToTicks()));
        Assert.That(input, Is.EqualTo(quantity.ToDecimal()));
    }

    [TestCase(123456789, 1.23456789)]
    [TestCase(-50000000, -0.5)]
    [TestCase(0, 0.0)]
    [TestCase(50000000000, 500.0)]
    public void FromTicks_CreatesCorrectDecimal(long inputTicks, decimal expectedDecimal)
    {
        // Arrange & Act
        var quantity = Quantity.FromTicks(inputTicks);

        // Assert
        Assert.That(expectedDecimal, Is.EqualTo(quantity.ToDecimal()));
        Assert.That(inputTicks, Is.EqualTo(quantity.ToTicks()));
    }

    [Test]
    public void Arithmetic_AdditionAndSubtraction_WorkCorrectly()
    {
        // Arrange
        var q1 = Quantity.FromDecimal(10.12345678m);
        var q2 = Quantity.FromDecimal(5.87654321m);

        // Act
        var sum = q1 + q2;
        var difference = q1 - q2;

        // Note: Decimal arithmetic can have tiny precision errors.
        // It's safer to check the ticks or use a tolerance.
        Assert.That(sum.ToDecimal(), Is.EqualTo(15.99999999m));
        Assert.That(difference.ToDecimal(), Is.EqualTo(4.24691357m));

        Assert.That(sum.ToTicks(), Is.EqualTo(1599999999L));
        Assert.That(difference.ToTicks(), Is.EqualTo(424691357L));
    }

    [Test]
    public void Comparison_Operators_WorkCorrectly()
    {
        // Arrange
        var q1 = Quantity.FromDecimal(1.0m);
        var q2 = Quantity.FromDecimal(2.0m);
        var q1_copy = Quantity.FromDecimal(1.0m);

        // Assert
        Assert.That(q1 == q1_copy, Is.True);
        Assert.That(q1 != q2, Is.True);
        Assert.That(q2 > q1, Is.True);
        Assert.That(q1 < q2, Is.True);
        Assert.That(q1 >= q1_copy, Is.True);
        Assert.That(q1 <= q1_copy, Is.True);
        Assert.That(q1 > q2, Is.False);
    }

    [Test]
    public void FromDecimal_WithExcessPrecision_TruncatesValue()
    {
        // Arrange: Input has more than 8 decimal places
        var preciseDecimal = 0.123456789123m;
        var expectedTruncatedDecimal = 0.12345678m;
        long expectedTicks = (long)(expectedTruncatedDecimal * QuantityScale);

        // Act
        var quantity = Quantity.FromDecimal(preciseDecimal);

        // Assert
        Assert.That(expectedTicks, Is.EqualTo(quantity.ToTicks()));
        Assert.That(expectedTruncatedDecimal, Is.EqualTo(quantity.ToDecimal()));
    }
}