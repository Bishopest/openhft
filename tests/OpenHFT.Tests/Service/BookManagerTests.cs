using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using OpenHFT.Core.Books;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Service;

namespace OpenHFT.Tests.Service;

[TestFixture]
public class BookManagerTests
{
    private Mock<ILogger<BookManager>> _mockLogger;
    private Mock<IOrderRouter> _mockOrderRouter;
    private Mock<IInstrumentRepository> _mockRepo;
    private BookManager _bookManager;

    private const int TestInstrumentId = 1;
    private const string TestBookName = "BTC";

    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger<BookManager>>();
        _mockOrderRouter = new Mock<IOrderRouter>();
        _mockRepo = new Mock<IInstrumentRepository>();

        // BookManager 생성. InitializeBooks가 호출되지만, 테스트를 위해 내부 상태를 직접 설정
        _bookManager = new BookManager(_mockLogger.Object, _mockOrderRouter.Object, _mockRepo.Object);

        // private _elements 필드에 테스트용 초기 BookElement를 추가하기 위해 리플렉션 사용
        var elementsField = typeof(BookManager).GetField("_elements", BindingFlags.NonPublic | BindingFlags.Instance);
        var elementsDict = elementsField.GetValue(_bookManager) as System.Collections.Concurrent.ConcurrentDictionary<int, BookElement>;
        elementsDict[TestInstrumentId] = new BookElement(TestBookName, TestInstrumentId, Price.FromDecimal(0m), Quantity.FromDecimal(0m), 0, 0, 0);
    }

    // private OnOrderFilled 메서드를 호출하기 위한 리플렉션 헬퍼
    private void SimulateFill(Fill fill)
    {
        var method = typeof(BookManager).GetMethod("OnOrderFilled", BindingFlags.NonPublic | BindingFlags.Instance);
        method.Invoke(_bookManager, new object[] { this, fill });
    }

    [Test]
    public void OnOrderFilled_WithInitialBuy_ShouldUpdateElementCorrectly()
    {
        // Arrange
        var fill = new Fill(TestInstrumentId, 1, "exo1", "exec1", Side.Buy,
                            Price.FromDecimal(100m), Quantity.FromDecimal(10m), 0);

        // Act
        SimulateFill(fill);

        // Assert
        var element = _bookManager.GetBookElement(TestInstrumentId);
        element.Quantity.ToDecimal().Should().Be(10m);
        element.AvgPrice.ToDecimal().Should().Be(100m);
        element.RealizedPnL.Should().Be(0m);
        element.VolumeInUsdt.Should().Be(1000m); // 100 * 10
    }

    [Test]
    public void OnOrderFilled_WithAdditionalBuy_ShouldUpdateAveragePrice()
    {
        // Arrange
        var fill1 = new Fill(TestInstrumentId, 1, "exo1", "exec1", Side.Buy, Price.FromDecimal(100m), Quantity.FromDecimal(10m), 0);
        SimulateFill(fill1);

        var fill2 = new Fill(TestInstrumentId, 2, "exo2", "exec2", Side.Buy, Price.FromDecimal(90m), Quantity.FromDecimal(10m), 0);
        // Expected Avg Price = (10 * 100 + 10 * 90) / (10 + 10) = 95

        // Act
        SimulateFill(fill2);

        // Assert
        var element = _bookManager.GetBookElement(TestInstrumentId);
        element.Quantity.ToDecimal().Should().Be(20m);
        element.AvgPrice.ToDecimal().Should().Be(95m);
        element.RealizedPnL.Should().Be(0m);
    }

    [Test]
    public void OnOrderFilled_WithPartialSell_ShouldCalculateRealizedPnl()
    {
        // Arrange
        var fill1 = new Fill(TestInstrumentId, 1, "exo1", "exec1", Side.Buy, Price.FromDecimal(100m), Quantity.FromDecimal(10m), 0);
        SimulateFill(fill1);

        var fill2 = new Fill(TestInstrumentId, 2, "exo2", "exec2", Side.Sell, Price.FromDecimal(110m), Quantity.FromDecimal(4m), 0);
        // PnL = (110 - 100) * 4 = 40

        // Act
        SimulateFill(fill2);

        // Assert
        var element = _bookManager.GetBookElement(TestInstrumentId);
        element.Quantity.ToDecimal().Should().Be(6m); // 10 - 4
        element.AvgPrice.ToDecimal().Should().Be(100m, "Avg price should not change on partial close.");
        element.RealizedPnL.Should().Be(40m);
    }

    [Test]
    public void OnOrderFilled_WithPositionFlipFromLongToShort_ShouldUpdateCorrectly()
    {
        // Arrange
        var fill1 = new Fill(TestInstrumentId, 1, "exo1", "exec1", Side.Buy, Price.FromDecimal(100m), Quantity.FromDecimal(10m), 0);
        SimulateFill(fill1);

        var fill2 = new Fill(TestInstrumentId, 2, "exo2", "exec2", Side.Sell, Price.FromDecimal(120m), Quantity.FromDecimal(15m), 0);
        // Position closed: 10 contracts. Realized PnL = (120 - 100) * 10 = 200
        // New position: -5 contracts @ 120

        // Act
        SimulateFill(fill2);

        // Assert
        var element = _bookManager.GetBookElement(TestInstrumentId);
        element.Quantity.ToDecimal().Should().Be(-5m); // 10 - 15
        element.AvgPrice.ToDecimal().Should().Be(120m, "Avg price should reset to the flipping trade's price.");
        element.RealizedPnL.Should().Be(200m);
    }

    [Test]
    public void OnOrderFilled_WithFillForUnmanagedInstrument_ShouldDoNothing()
    {
        // Arrange
        var unmanagedFill = new Fill(999, 1, "exo1", "exec1", Side.Buy, Price.FromDecimal(100m), Quantity.FromDecimal(10m), 0);

        // Act
        SimulateFill(unmanagedFill);

        // Assert
        var element = _bookManager.GetBookElement(TestInstrumentId);
        // Element should still be in its initial zero state
        element.Quantity.ToTicks().Should().Be(0);

        // Event should not have been fired
        // This requires mocking the event handler subscription, which is more complex.
        // For now, we verify the state change didn't happen.
    }
}