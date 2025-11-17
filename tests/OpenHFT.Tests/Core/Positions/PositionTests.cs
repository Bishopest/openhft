using FluentAssertions;
using NUnit.Framework;
using OpenHFT.Core.Models;

namespace OpenHFT.Tests.Core.Positions;

public class PositionTests
{
    private const int TestInstrumentId = 1;

    // --- 매수 (Long) 포지션 시나리오 ---

    [Test]
    public void ApplyFill_OnZeroPosition_WithNewBuy_ShouldCreateLongPosition()
    {
        // Arrange
        var initialPosition = Position.Zero(TestInstrumentId);
        var fill = new Fill(TestInstrumentId, 1, "exo1", "exe1", Side.Buy,
                            Price.FromDecimal(100m), Quantity.FromDecimal(10m), 1);

        // Act
        var newPosition = initialPosition.ApplyFill(fill);

        // Assert
        newPosition.InstrumentId.Should().Be(TestInstrumentId);
        newPosition.Quantity.ToDecimal().Should().Be(10m);
        newPosition.AverageEntryPrice.ToDecimal().Should().Be(100m); newPosition.LastUpdateTime.Should().Be(1);
    }

    [Test]
    public void ApplyFill_OnLongPosition_WithAnotherBuy_ShouldIncreasePositionAndAverageDown()
    {
        // Arrange
        var initialPosition = new Position(TestInstrumentId, Quantity.FromDecimal(10m), Price.FromDecimal(100m), 1);
        var fill = new Fill(TestInstrumentId, 2, "exo2", "exe2", Side.Buy,
                            Price.FromDecimal(90m), Quantity.FromDecimal(10m), 2);

        // Expected Avg Price = (10 * 100 + 10 * 90) / (10 + 10) = 1900 / 20 = 95
        var expectedAvgPrice = Price.FromDecimal(95m);

        // Act
        var newPosition = initialPosition.ApplyFill(fill);

        // Assert
        newPosition.Quantity.ToDecimal().Should().Be(20m);
        newPosition.AverageEntryPrice.Should().Be(expectedAvgPrice);
        newPosition.LastUpdateTime.Should().Be(2);
    }

    [Test]
    public void ApplyFill_OnLongPosition_WithPartialSell_ShouldDecreasePosition()
    {
        // Arrange
        var initialPosition = new Position(TestInstrumentId, Quantity.FromDecimal(20m), Price.FromDecimal(95m), 2);
        var fill = new Fill(TestInstrumentId, 3, "exo3", "exe3", Side.Sell,
                            Price.FromDecimal(110m), Quantity.FromDecimal(5m), 3);

        // Act
        var newPosition = initialPosition.ApplyFill(fill);

        // Assert
        newPosition.Quantity.ToDecimal().Should().Be(15m); // 20 - 5 = 15
        newPosition.AverageEntryPrice.ToDecimal().Should().Be(95m, "because taking profit should not change the average entry price of the remaining position.");
        newPosition.LastUpdateTime.Should().Be(3);
    }

    [Test]
    public void ApplyFill_OnLongPosition_WithFullSell_ShouldFlattenPosition()
    {
        // Arrange
        var initialPosition = new Position(TestInstrumentId, Quantity.FromDecimal(15m), Price.FromDecimal(95m), 3);
        var fill = new Fill(TestInstrumentId, 4, "exo4", "exe4", Side.Sell,
                            Price.FromDecimal(120m), Quantity.FromDecimal(15m), 4);

        // Act
        var newPosition = initialPosition.ApplyFill(fill);

        // Assert
        newPosition.Quantity.ToTicks().Should().Be(0);
        newPosition.AverageEntryPrice.ToTicks().Should().Be(0, "because a flat position has no entry price.");
        newPosition.LastUpdateTime.Should().Be(4);
    }

    [Test]
    public void ApplyFill_OnLongPosition_WithFlippingSell_ShouldCreateShortPosition()
    {
        // Arrange
        var initialPosition = new Position(TestInstrumentId, Quantity.FromDecimal(10m), Price.FromDecimal(100m), 4);
        var fill = new Fill(TestInstrumentId, 5, "exo5", "exe5", Side.Sell,
                            Price.FromDecimal(130m), Quantity.FromDecimal(25m), 5);

        // Act
        var newPosition = initialPosition.ApplyFill(fill);

        // Assert
        newPosition.Quantity.ToDecimal().Should().Be(-15m); // 10 - 25 = -15
        newPosition.AverageEntryPrice.ToDecimal().Should().Be(130m, "because when a position flips, the new entry price is the price of the flipping trade.");
        newPosition.LastUpdateTime.Should().Be(5);
    }

    // --- 매도 (Short) 포지션 시나리오 ---

    [Test]
    public void ApplyFill_OnZeroPosition_WithNewSell_ShouldCreateShortPosition()
    {
        // Arrange
        var initialPosition = Position.Zero(TestInstrumentId);
        var fill = new Fill(TestInstrumentId, 1, "exo1", "exe1", Side.Sell,
                            Price.FromDecimal(200m), Quantity.FromDecimal(5m), 0);

        // Act
        var newPosition = initialPosition.ApplyFill(fill);

        // Assert
        newPosition.Quantity.ToDecimal().Should().Be(-5m);
        newPosition.AverageEntryPrice.ToDecimal().Should().Be(200m);
    }

    [Test]
    public void ApplyFill_OnShortPosition_WithAnotherSell_ShouldIncreaseShortPositionAndAverageUp()
    {
        // Arrange
        var initialPosition = new Position(TestInstrumentId, Quantity.FromDecimal(-5m), Price.FromDecimal(200m), 1);
        var fill = new Fill(TestInstrumentId, 2, "exo2", "exe2", Side.Sell,
                            Price.FromDecimal(210m), Quantity.FromDecimal(5m), 0);

        // Expected Avg Price = ((-5 * 200) + (-5 * 210)) / (-5 + -5) = -2050 / -10 = 205
        var expectedAvgPrice = Price.FromDecimal(205m);

        // Act
        var newPosition = initialPosition.ApplyFill(fill);

        // Assert
        newPosition.Quantity.ToDecimal().Should().Be(-10m);
        newPosition.AverageEntryPrice.Should().Be(expectedAvgPrice);
    }

    [Test]
    public void ApplyFill_OnShortPosition_WithPartialBuy_ShouldDecreaseShortPosition()
    {
        // Arrange
        var initialPosition = new Position(TestInstrumentId, Quantity.FromDecimal(-10m), Price.FromDecimal(205m), 2);
        var fill = new Fill(TestInstrumentId, 3, "exo3", "exe3", Side.Buy,
                            Price.FromDecimal(190m), Quantity.FromDecimal(3m), 0);

        // Act
        var newPosition = initialPosition.ApplyFill(fill);

        // Assert
        newPosition.Quantity.ToDecimal().Should().Be(-7m); // -10 + 3 = -7
        newPosition.AverageEntryPrice.ToDecimal().Should().Be(205m, "because covering part of a short should not change the average entry price.");
    }

    [Test]
    public void ApplyFill_OnShortPosition_WithFlippingBuy_ShouldCreateLongPosition()
    {
        // Arrange
        var initialPosition = new Position(TestInstrumentId, Quantity.FromDecimal(-10m), Price.FromDecimal(205m), 3);
        var fill = new Fill(TestInstrumentId, 4, "exo4", "exe4", Side.Buy,
                            Price.FromDecimal(180m), Quantity.FromDecimal(15m), 0);

        // Act
        var newPosition = initialPosition.ApplyFill(fill);

        // Assert
        newPosition.Quantity.ToDecimal().Should().Be(5m); // -10 + 15 = 5
        newPosition.AverageEntryPrice.ToDecimal().Should().Be(180m, "because when a position flips, the new entry price is the price of the flipping trade.");
    }
}