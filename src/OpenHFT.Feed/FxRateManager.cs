using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;

namespace OpenHFT.Feed;

public class FxRateManager : IFxRateService
{
    private readonly ILogger<FxRateManager> _logger;
    private readonly IInstrumentRepository _instrumentRepository;
    private readonly IMarketDataManager _marketDataManager;

    // [설정] 참조 거래소 및 상품 타입 (Binance Perp이 가장 유동성 풍부)
    public static readonly ExchangeEnum ReferenceExchange = ExchangeEnum.BINANCE;
    public static readonly ProductType ReferenceProductType = ProductType.PerpetualFuture;

    // [설정] 환율 참조용 필수 통화 목록
    // 이 목록에 있는 통화들 간의 조합(Pair)을 앱 시작 시 자동 구독하도록 유도함
    public static readonly List<Currency> ReferenceCurrencies = new()
    {
        Currency.BTC,
        Currency.USDT,
        // 필요 시 ETH 등 추가 
    };

    // 성능 최적화를 위한 캐시 (SourceCurrency + TargetCurrency -> Instrument + IsInverted)
    private readonly ConcurrentDictionary<(Currency, Currency), (Instrument Instrument, bool IsInverted)?> _conversionPathCache = new();

    public FxRateManager(
        ILogger<FxRateManager> logger,
        IInstrumentRepository instrumentRepository,
        IMarketDataManager marketDataManager)
    {
        _logger = logger;
        _instrumentRepository = instrumentRepository;
        _marketDataManager = marketDataManager;
    }

    public CurrencyAmount? Convert(CurrencyAmount source, Currency target)
    {
        if (source.Currency == target) return source;

        // 1. 변환 경로 찾기 (캐싱 활용)
        var path = GetConversionPath(source.Currency, target);

        if (path == null)
        {
            _logger.LogWarningWithCaller($"No conversion path found for {source.Currency} -> {target}");
            return null;
        }

        var (instrument, isInverted) = path.Value;

        // 2. 시세 조회 (OrderBook 구독 필요 없이 MarketDataManager에서 즉시 조회)
        // OrderBook이 없으면 BestOrderBook 시도 (가볍게)
        var ob = _marketDataManager.GetBestOrderBook(instrument.InstrumentId);
        if (ob is null)
        {
            _logger.LogWarningWithCaller($"No order book exists on {instrument.Symbol}, can not convert currency rate");
            return null;
        }
        var midPrice = ob.GetMidPrice();

        if (midPrice.ToDecimal() <= 0)
        {
            // _logger.LogDebug($"Price not available for {instrument.Symbol}. Cannot convert.");
            return null;
        }

        // 3. 계산
        decimal resultAmount;
        if (isInverted)
        {
            // 예: Source=USDT, Target=BTC, Inst=BTC/USDT.
            // USDT / Price = BTC
            resultAmount = source.Amount / midPrice.ToDecimal();
        }
        else
        {
            // 예: Source=BTC, Target=USDT, Inst=BTC/USDT.
            // BTC * Price = USDT
            resultAmount = source.Amount * midPrice.ToDecimal();
        }

        return new CurrencyAmount(resultAmount, target);
    }

    private (Instrument Instrument, bool IsInverted)? GetConversionPath(Currency source, Currency target)
    {
        var key = (source, target);
        if (_conversionPathCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // 1. 정방향 찾기 (Base=Source, Quote=Target) 예: BTC -> USDT (BTC/USDT)
        var forwardInst = _instrumentRepository.GetAll().Where(i =>
            i.BaseCurrency == source &&
            i.QuoteCurrency == target &&
            i.SourceExchange == ReferenceExchange &&
            i.ProductType == ReferenceProductType
        ).FirstOrDefault();

        if (forwardInst != null)
        {
            var result = (forwardInst, false);
            _conversionPathCache[key] = result;
            return result;
        }

        // 2. 역방향 찾기 (Base=Target, Quote=Source) 예: USDT -> BTC (BTC/USDT)
        var inverseInst = _instrumentRepository.GetAll().Where(i =>
            i.BaseCurrency == target &&
            i.QuoteCurrency == source &&
            i.SourceExchange == ReferenceExchange &&
            i.ProductType == ReferenceProductType
        ).FirstOrDefault();

        if (inverseInst != null)
        {
            var result = (inverseInst, true);
            _conversionPathCache[key] = result;
            return result;
        }

        // 경로 없음
        _conversionPathCache[key] = null;
        return null;
    }
}
