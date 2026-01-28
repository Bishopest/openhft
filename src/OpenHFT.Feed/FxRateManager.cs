using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;

namespace OpenHFT.Feed;

public class FxRateManager : FxRateManagerBase
{
    private readonly IMarketDataManager _marketDataManager;

    // Constructor updated: no longer needs List<FxRateConfig>
    public FxRateManager(
        ILogger<FxRateManager> logger,
        IInstrumentRepository instrumentRepository,
        IMarketDataManager marketDataManager)
        : base(logger, instrumentRepository) // Pass to base
    {
        _marketDataManager = marketDataManager;
    }

    protected override Price? GetConversionPrice(int instrumentId)
    {
        var ob = _marketDataManager.GetOrderBook(instrumentId);
        if (ob == null) return null;

        // 1. 최우선 호가 정보 조회
        // Zero-allocation을 위해 구조체 반환 메서드 사용 (ref readonly 권장)
        var (bestBidPrice, bestBidQty) = ob.GetBestBid();
        var (bestAskPrice, bestAskQty) = ob.GetBestAsk();

        decimal bidP = bestBidPrice.ToDecimal();
        decimal askP = bestAskPrice.ToDecimal();
        decimal bidQ = bestBidQty.ToDecimal();
        decimal askQ = bestAskQty.ToDecimal();

        // 2. 유효성 검사 (가격이 없거나 0이면 계산 불가)
        if (bidP <= 0 || askP <= 0 || bidQ <= 0 || askQ <= 0 || bidP >= askP)
        {
            return null;
        }

        // 3. 스프레드 체크
        if (!ob.IsTightSpread())
        {
            return ob.GetMidPrice();
        }

        // 4. 잔량 가중 평균 가격 (Weighted Mid Price) 계산
        decimal totalQty = bidQ + askQ;
        decimal weightedPrice = bidP + (askP - bidP) * (bidQ / totalQty);

        return Price.FromDecimal(weightedPrice);
    }
}
