using System;
using Disruptor;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;

namespace OpenHFT.Processing;

public interface IOrderUpdateHandler : IEventHandler<OrderStatusReportWrapper> { }
public class OrderUpdateDistributor : IOrderUpdateHandler
{
    private readonly ILogger<OrderUpdateDistributor> _logger;
    private readonly IOrderRouter _orderRouter;

    public OrderUpdateDistributor(ILogger<OrderUpdateDistributor> logger, IOrderRouter orderRouter)
    {
        _logger = logger;
        _orderRouter = orderRouter;
    }

    public void OnEvent(OrderStatusReportWrapper data, long sequence, bool endOfBatch)
    {
        try
        {
            // Disruptor로부터 받은 Report를 OrderRouter에게 전달
            _orderRouter.RouteReport(data.Report);
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"Error distributing order status report at sequence {sequence}");
        }
    }

}
