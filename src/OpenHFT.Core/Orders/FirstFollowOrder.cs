using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Orders;

public class FirstFollowOrder : AlgoOrder
{
    public override AlgoOrderType AlgoOrderType => AlgoOrderType.FirstFollow;
    private readonly Instrument _instrument;

    // Instrument가 추가로 필요함 (TickSize 계산용)
    public FirstFollowOrder(
        long clientOrderId,
        Instrument instrument,
        Side side,
        string bookName,
        IOrderRouter router,
        IOrderGateway gateway,
        ILogger<Order> logger,
        IMarketDataManager marketDataManager)
        : base(clientOrderId, instrument.InstrumentId, side, bookName, router, gateway, logger, marketDataManager)
    {
        _instrument = instrument;
    }

    protected override Price CalculateTargetPrice(OrderBook book)
    {
        var tickSize = _instrument.TickSize;

        if (Side == Side.Buy)
        {
            var (bestBid, _) = book.GetBestBid();
            if (bestBid.ToTicks() <= 0) return Price.FromTicks(0);

            // Self-Pennying 방지: 내가 이미 1등(내 가격 >= 시장가)이면 내 가격 유지
            // 주의: 초기 진입 시점(this.Price == 0)엔 조건문이 거짓이 되어 bestBid + tickSize가 됨 (정상)
            if (this.Price.ToTicks() > 0 && this.Price >= bestBid)
            {
                return this.Price;
            }
            return bestBid + tickSize;
        }
        else // Side.Sell
        {
            var (bestAsk, _) = book.GetBestAsk();
            if (bestAsk.ToTicks() <= 0) return Price.FromTicks(0);

            // Self-Pennying 방지
            if (this.Price.ToTicks() > 0 && this.Price <= bestAsk)
            {
                return this.Price;
            }
            return bestAsk - tickSize;
        }
    }
}
