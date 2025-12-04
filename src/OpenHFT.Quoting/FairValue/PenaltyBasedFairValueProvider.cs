using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Quoting.Interfaces;

namespace OpenHFT.Quoting.FairValue;

public class PenaltyBasedFairValueProvider : AbstractFairValueProvider, IBestOrderBookConsumerProvider
{
    private readonly decimal _expandMultiplier;
    private readonly decimal _shrinkMultiplier;

    private Price? _prevAsk;
    private Price? _prevBid;

    private decimal _accAskPenalty;
    private decimal _accBidPenalty;

    public override FairValueModel Model => FairValueModel.Penalty;

    public PenaltyBasedFairValueProvider(ILogger logger, int instrumentId,
        decimal expandMultiplier = 1.0m,
        decimal shrinkMultiplier = 2.0m)
        : base(logger, instrumentId)
    {
        _expandMultiplier = expandMultiplier;
        _shrinkMultiplier = shrinkMultiplier;
        _prevAsk = null;
        _prevBid = null;
        _accAskPenalty = 0.0m;
        _accBidPenalty = 0.0m;
    }

    public void Update(BestOrderBook bob)
    {
        if (bob.InstrumentId != SourceInstrumentId)
            return;

        var bestAsk = bob.GetBestAsk().price;
        var bestBid = bob.GetBestBid().price;

        if (bestAsk.ToDecimal() <= 0m || bestBid.ToDecimal() <= 0m)
        {
            Logger.LogWarningWithCaller($"Invalid best price (<=0) on instrument {bob.InstrumentId}");
            return;
        }

        if (_prevAsk is null || _prevBid is null)
        {
            _prevAsk = bestAsk;
            _prevBid = bestBid;
            // Logger.LogInformationWithCaller($"[INIT] ask={bestAsk.ToDecimal()}, bid={bestBid.ToDecimal()}");
            return;
        }

        decimal askNow = bestAsk.ToDecimal();
        decimal bidNow = bestBid.ToDecimal();
        decimal askPrev = _prevAsk.Value.ToDecimal();
        decimal bidPrev = _prevBid.Value.ToDecimal();

        decimal askDiff = askNow - askPrev;
        decimal bidDiff = bidNow - bidPrev;

        decimal spreadNow = askNow - bidNow;
        decimal spreadPrev = askPrev - bidPrev;
        decimal spreadChange = spreadNow - spreadPrev;

        // Logger.LogInformationWithCaller(
        // $"[DIFF] askDiff={askDiff}, bidDiff={bidDiff}, " +
        // $"spreadPrev={spreadPrev}, spreadNow={spreadNow}, spreadChange={spreadChange}"
        // );

        bool isAskDominant = Math.Abs(askDiff) >= Math.Abs(bidDiff);
        string dom = isAskDominant ? "ASK" : "BID";
        // expand
        if (spreadChange > 0)
        {
            bool askDominant = Math.Abs(askDiff) >= Math.Abs(bidDiff);
            decimal delta = spreadChange * _expandMultiplier;
            // Logger.LogInformationWithCaller(
            //     $"[EXPAND] spreadChange={spreadChange}, dominant={dom}, " +
            //     $"delta={delta}, multip={_expandMultiplier}"
            // );
            if (askDominant)
                _accAskPenalty = Math.Max(0, _accAskPenalty + delta);
            else
                _accBidPenalty = Math.Max(0, _accBidPenalty + delta);
        }
        else if (spreadChange < 0)
        {
            bool askDominant = Math.Abs(askDiff) >= Math.Abs(bidDiff);
            decimal delta = spreadChange * _shrinkMultiplier; // shrink 는 보상 증가
            // Logger.LogInformationWithCaller(
            //     $"[SHRINK] spreadChange={spreadChange}, dominant={dom}, " +
            //     $"delta={delta}, multip={_shrinkMultiplier}"
            // );

            if (askDominant)
                _accAskPenalty = Math.Max(0, _accAskPenalty + delta);
            else
                _accBidPenalty = Math.Max(0, _accBidPenalty + delta);
        }

        // Logger.LogInformationWithCaller($"[PENALTY] accAsk={_accAskPenalty}, accBid={_accBidPenalty}");

        // fair 값 계산
        var fairAsk = Price.FromDecimal(bidNow + _accAskPenalty);
        var fairBid = Price.FromDecimal(askNow - _accBidPenalty);

        // Logger.LogInformationWithCaller(
        // $"[FAIR] bestBid={bidNow} -> fairAsk={fairAsk.ToDecimal()}, " +
        // $"bestAsk={askNow} -> fairBid={fairBid.ToDecimal()}"
        // );

        _prevAsk = bestAsk;
        _prevBid = bestBid;

        if (fairAsk != _lastFairAskValue || fairBid != _lastFairBidValue)
        {
            var fairValueUpdate = new FairValueUpdate(SourceInstrumentId, fairAsk, fairBid);
            _lastFairAskValue = fairValueUpdate.FairAskValue;
            _lastFairBidValue = fairValueUpdate.FairBidValue;
            OnFairValueChanged(fairValueUpdate);
        }
    }
}
