using System;
using Castle.Core.Logging;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Orders;

namespace OpenHFT.Tests.Gateway.Helpers;

public class TestableOrder : Order
{
    private readonly TaskCompletionSource<OrderStatusReport> _tcs = new();
    private readonly Func<OrderStatusReport, bool> _completionCondition;

    public TestableOrder(int instrumentId, Side side, IOrderRouter router, IOrderGateway gateway, Func<OrderStatusReport, bool> completionCondition, ILogger<Order> logger)
        : base(instrumentId, side, router, gateway, logger)
    {
        _completionCondition = completionCondition;
        StatusChanged += OnStatusChanged;
    }

    private void OnStatusChanged(object? sender, OrderStatusReport report)
    {
        if (_completionCondition(report))
        {
            _tcs.SetResult(report);
        }
    }

    public Task<OrderStatusReport> WaitForCompletionAsync(TimeSpan timeout)
    {
        return _tcs.Task.WaitAsync(timeout);
    }
}

public class TestableOrderFactory : IOrderFactory
{
    private readonly IOrderRouter _router;
    private readonly IOrderGateway _gateway;
    private readonly ILogger<TestableOrder> _logger;
    public Func<OrderStatusReport, bool> CompletionCondition { get; set; } = _ => false;

    public TestableOrderFactory(IOrderRouter router, IOrderGateway gateway, ILogger<TestableOrder> logger)
    {
        _router = router;
        _gateway = gateway;
        _logger = logger;
    }

    public IOrder Create(int instrumentId, Side side)
    {
        return new TestableOrder(instrumentId, side, _router, _gateway, CompletionCondition, _logger);
    }
}