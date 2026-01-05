using System.Runtime.CompilerServices;
using FluentAssertions;
using NUnit.Framework;
using OpenHFT.Core.Models;

namespace OpenHFT.Tests.Core.Models;

public class BookModelTests
{
    [Test]
    public void BookSide_InitialState_ShouldBeEmpty()
    {
        // Arrange
        var side = new BookSide(Side.Buy, 10);

        // Assert
        side.LevelCount.Should().Be(0);
        Unsafe.IsNullRef(in side.GetBestLevel()).Should().BeTrue();
    }

    [Test]
    public void BookSide_BuySide_ShouldSortDescending()
    {
        // Arrange: 매수호가는 높은 가격이 우선 (내림차순)
        var side = new BookSide(Side.Buy, 10);
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act: 100, 120, 110 순서로 입력
        side.UpdateLevel(Price.FromDecimal(100), Quantity.FromDecimal(1), 1, ts);
        side.UpdateLevel(Price.FromDecimal(120), Quantity.FromDecimal(1), 2, ts);
        side.UpdateLevel(Price.FromDecimal(110), Quantity.FromDecimal(1), 3, ts);

        // Assert
        side.LevelCount.Should().Be(3);
        side.GetLevelAt(0).Price.ToDecimal().Should().Be(120); // Best
        side.GetLevelAt(1).Price.ToDecimal().Should().Be(110);
        side.GetLevelAt(2).Price.ToDecimal().Should().Be(100);
    }

    [Test]
    public void BookSide_SellSide_ShouldSortAscending()
    {
        // Arrange: 매도호가는 낮은 가격이 우선 (오름차순)
        var side = new BookSide(Side.Sell, 10);
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act: 100, 80, 90 순서로 입력
        side.UpdateLevel(Price.FromDecimal(100), Quantity.FromDecimal(1), 1, ts);
        side.UpdateLevel(Price.FromDecimal(80), Quantity.FromDecimal(1), 2, ts);
        side.UpdateLevel(Price.FromDecimal(90), Quantity.FromDecimal(1), 3, ts);

        // Assert
        side.LevelCount.Should().Be(3);
        side.GetLevelAt(0).Price.ToDecimal().Should().Be(80); // Best
        side.GetLevelAt(1).Price.ToDecimal().Should().Be(90);
        side.GetLevelAt(2).Price.ToDecimal().Should().Be(100);
    }

    [Test]
    public void BookSide_UpdateExistingLevel_ShouldModifyInPlace()
    {
        // Arrange
        var side = new BookSide(Side.Buy, 10);
        var price = Price.FromDecimal(100);
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act
        side.UpdateLevel(price, Quantity.FromDecimal(10), 1, ts);
        side.UpdateLevel(price, Quantity.FromDecimal(15), 2, ts); // 수량 변경

        // Assert
        side.LevelCount.Should().Be(1);
        side.GetBestLevel().TotalQuantity.ToDecimal().Should().Be(15);
        side.GetBestLevel().LastUpdateSequence.Should().Be(2);
    }

    [Test]
    public void BookSide_DeleteLevel_ShouldShiftArrayCorrectly()
    {
        // Arrange
        var side = new BookSide(Side.Buy, 10);
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        side.UpdateLevel(Price.FromDecimal(120), Quantity.FromDecimal(1), 1, ts);
        side.UpdateLevel(Price.FromDecimal(110), Quantity.FromDecimal(1), 2, ts);
        side.UpdateLevel(Price.FromDecimal(100), Quantity.FromDecimal(1), 3, ts);

        // Act: 중간 레벨(110) 삭제
        side.UpdateLevel(Price.FromDecimal(110), Quantity.FromDecimal(0), 4, ts);

        // Assert
        side.LevelCount.Should().Be(2);
        side.GetLevelAt(0).Price.ToDecimal().Should().Be(120);
        side.GetLevelAt(1).Price.ToDecimal().Should().Be(100); // 110이 삭제되어 100이 올라옴
    }

    [Test]
    public void BookSide_BoundaryCondition_ShouldNotOverflow()
    {
        // Arrange: 최대 뎁스를 3으로 제한
        int maxDepth = 3;
        var side = new BookSide(Side.Buy, maxDepth);
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act: 4개의 서로 다른 가격 입력
        side.UpdateLevel(Price.FromDecimal(100), Quantity.FromDecimal(1), 1, ts);
        side.UpdateLevel(Price.FromDecimal(200), Quantity.FromDecimal(1), 2, ts);
        side.UpdateLevel(Price.FromDecimal(300), Quantity.FromDecimal(1), 3, ts);

        // 300, 200, 100 이 들어있는 상태에서 150 입력 (가장 끝의 100이 밀려나야 함)
        side.UpdateLevel(Price.FromDecimal(150), Quantity.FromDecimal(1), 4, ts);

        // Assert
        side.LevelCount.Should().Be(3);
        side.GetLevelAt(0).Price.ToDecimal().Should().Be(300);
        side.GetLevelAt(1).Price.ToDecimal().Should().Be(200);
        side.GetLevelAt(2).Price.ToDecimal().Should().Be(150);

        // 100은 배열에서 사라졌어야 함
        side.FindIndex(Price.FromDecimal(100)).Should().BeNegative();
    }

    [Test]
    public void BookSide_GetDepth_ShouldCalculateCorrectSum()
    {
        // Arrange
        var side = new BookSide(Side.Buy, 10);
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        side.UpdateLevel(Price.FromDecimal(100), Quantity.FromDecimal(10), 1, ts);
        side.UpdateLevel(Price.FromDecimal(90), Quantity.FromDecimal(20), 2, ts);
        side.UpdateLevel(Price.FromDecimal(80), Quantity.FromDecimal(30), 3, ts);

        // Act
        var totalQty = side.GetDepth(2); // 상위 2개 레벨의 합 (10 + 20)

        // Assert
        totalQty.ToDecimal().Should().Be(30);
    }

    [Test]
    public void BookSide_Clear_ShouldResetCount()
    {
        // Arrange
        var side = new BookSide(Side.Buy, 10);
        side.UpdateLevel(Price.FromDecimal(100), Quantity.FromDecimal(10), 1, 0);

        // Act
        side.Clear();

        // Assert
        side.LevelCount.Should().Be(0);
        Unsafe.IsNullRef(in side.GetBestLevel()).Should().BeTrue();
    }
}