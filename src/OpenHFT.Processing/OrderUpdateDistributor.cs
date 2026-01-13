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
            var report = data.Report;
            if (report.ClientOrderId > 0)
            {
                // Standard path for exchanges that support ClientOrderId.
                _orderRouter.RouteReport(in report);
            }
            else if (!string.IsNullOrEmpty(report.ExchangeOrderId))
            {
                // Path for exchanges like Bithumb that only provide ExchangeOrderId.
                _orderRouter.RouteReportByExchangeId(in report);
            }
            else
            {
                _logger.LogWarningWithCaller($"Received an OrderStatusReport with no valid ID. Sequence: {sequence}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, $"Error distributing order status report at sequence {sequence}");
        }
    }

}
