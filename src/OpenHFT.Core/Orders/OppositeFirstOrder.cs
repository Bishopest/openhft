using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Orders;

public class OppositeFirstOrder : AlgoOrder
{
    public override AlgoOrderType AlgoOrderType => AlgoOrderType.OppositeFirst;
    public OppositeFirstOrder(
        long clientOrderId,
        int instrumentId,
        Side side,
        string bookName,
        IOrderRouter router,
        IOrderGateway gateway,
        ILogger<Order> logger,
        IMarketDataManager marketDataManager)
        : base(clientOrderId, instrumentId, side, bookName, router, gateway, logger, marketDataManager)
    {
    }

    protected override Price CalculateTargetPrice(OrderBook book)
    {
        if (Side == Side.Buy)
        {
            var (bestAsk, _) = book.GetBestAsk();
            return bestAsk;
        }
        else
        {
            var (bestBid, _) = book.GetBestBid();
            return bestBid;
        }
    }
}
