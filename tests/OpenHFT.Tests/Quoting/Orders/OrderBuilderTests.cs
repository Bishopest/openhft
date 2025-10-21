using Moq;
using NUnit.Framework;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;
namespace OpenHFT.Tests.Quoting.Orders;

[TestFixture]
public class OrderBuilderTests
{
    private Mock<IOrderFactory> _mockOrderFactory;
    private const int InstrumentId = 1;
    private const Side TestSide = Side.Buy;

    [SetUp]
    public void SetUp()
    {
        _mockOrderFactory = new Mock<IOrderFactory>();
    }

    [Test]
    public void Build_WithValidParameters_ShouldReturnConfiguredOrder()
    {
        // Arrange
        var expectedPrice = Price.FromDecimal(50000m);
        var expectedQuantity = Quantity.FromDecimal(1.5m);
        var expectedOrderType = OrderType.Limit;

        // The factory will return a real, but basic, Order object.
        var orderShell = new Order(InstrumentId, TestSide);
        _mockOrderFactory.Setup(f => f.Create(InstrumentId, TestSide)).Returns(orderShell);

        var builder = new OrderBuilder(_mockOrderFactory.Object, InstrumentId, TestSide);

        // Act
        var finalOrder = builder
            .WithPrice(expectedPrice)
            .WithQuantity(expectedQuantity)
            .WithOrderType(expectedOrderType)
            .Build();

        // Assert
        Assert.That(finalOrder, Is.Not.Null);
        Assert.That(InstrumentId, Is.EqualTo(finalOrder.InstrumentId));
        Assert.That(TestSide, Is.EqualTo(finalOrder.Side));
        Assert.That(expectedPrice, Is.EqualTo(finalOrder.Price));
        Assert.That(expectedQuantity, Is.EqualTo(finalOrder.Quantity));
        Assert.That(expectedQuantity, Is.EqualTo(finalOrder.LeavesQuantity), "LeavesQuantity should be initialized to Quantity.");

        // We need to cast to the concrete class to check the OrderType property
        Assert.That(finalOrder, Is.InstanceOf<Order>());
        var concreteOrder = (Order)finalOrder;
        Assert.That(concreteOrder.OrderType, Is.EqualTo(expectedOrderType));
    }

    [Test]
    public void Build_WithoutPrice_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var orderShell = new Order(InstrumentId, TestSide);
        _mockOrderFactory.Setup(f => f.Create(InstrumentId, TestSide)).Returns(orderShell);
        var builder = new OrderBuilder(_mockOrderFactory.Object, InstrumentId, TestSide);

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            builder.WithQuantity(Quantity.FromDecimal(1m)).Build();
        });
        Assert.That(ex.Message, Is.EqualTo("Order price and quantity must be positive."));
    }

    [Test]
    public void Build_WithoutQuantity_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var orderShell = new Order(InstrumentId, TestSide);
        _mockOrderFactory.Setup(f => f.Create(InstrumentId, TestSide)).Returns(orderShell);
        var builder = new OrderBuilder(_mockOrderFactory.Object, InstrumentId, TestSide);

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            builder.WithPrice(Price.FromDecimal(50000m)).Build();
        });
        Assert.That(ex.Message, Is.EqualTo("Order price and quantity must be positive."));
    }
}
