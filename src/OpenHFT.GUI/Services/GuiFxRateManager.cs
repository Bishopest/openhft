using System;
using System.Collections.Concurrent;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;

namespace OpenHFT.GUI.Services;

/// <summary>
/// A GUI-specific implementation of IFxRateService that uses the IOrderBookManager
/// as its source for market data to perform currency conversions.
/// </summary>
public class GuiFxRateManager : IFxRateService
{
    // These static properties can be shared or moved to a common config location.
    public static readonly ExchangeEnum ReferenceExchange = ExchangeEnum.BINANCE;
    public static readonly ProductType ReferenceProductType = ProductType.PerpetualFuture;

    private readonly ILogger<GuiFxRateManager> _logger;
    private readonly IInstrumentRepository _instrumentRepository;
    private readonly IOrderBookManager _orderBookManager; // Depends on the GUI's book manager

    // Cache for conversion paths remains the same.
    private readonly ConcurrentDictionary<(Currency, Currency), (Instrument Instrument, bool IsInverted)?> _conversionPathCache = new();

    public GuiFxRateManager(
        ILogger<GuiFxRateManager> logger,
        IInstrumentRepository instrumentRepository,
        IOrderBookManager orderBookManager) // Injects the GUI-specific service
    {
        _logger = logger;
        _instrumentRepository = instrumentRepository;
        _orderBookManager = orderBookManager;
    }

    public CurrencyAmount? Convert(CurrencyAmount source, Currency target)
    {
        if (source.Currency == target) return source;

        var path = GetConversionPath(source.Currency, target);

        if (path == null)
        {
            _logger.LogWarningWithCaller($"No conversion path found for {source.Currency} -> {target}");
            return null;
        }

        var (instrument, isInverted) = path.Value;

        var ob = _orderBookManager.GetOrderBook(instrument.InstrumentId);

        if (ob is null)
        {
            // This is expected if the reference instrument (e.g., BTCUSDT) is not yet subscribed.
            _logger.LogWarningWithCaller($"Order book for reference instrument {instrument.Symbol} not available for FX conversion.");
            return null;
        }

        var midPrice = ob.GetMidPrice();
        if (midPrice.ToDecimal() <= 0)
        {
            return null;
        }

        decimal resultAmount = isInverted
            ? source.Amount / midPrice.ToDecimal() // e.g., USDT / (BTC/USDT) = BTC
            : source.Amount * midPrice.ToDecimal(); // e.g., BTC * (BTC/USDT) = USDT

        return new CurrencyAmount(resultAmount, target);
    }

    // This private helper method can be copied directly from the original FxRateManager
    // as it only depends on IInstrumentRepository.
    private (Instrument Instrument, bool IsInverted)? GetConversionPath(Currency source, Currency target)
    {
        var key = (source, target);
        if (_conversionPathCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // 1. Forward path (Base=Source, Quote=Target) e.g., BTC -> USDT (BTC/USDT)
        var forwardInst = _instrumentRepository.GetAll().FirstOrDefault(i =>
            i.BaseCurrency == source &&
            i.QuoteCurrency == target &&
            i.SourceExchange == ReferenceExchange &&
            i.ProductType == ReferenceProductType
        );

        if (forwardInst != null)
        {
            var result = (forwardInst, false);
            _conversionPathCache[key] = result;
            return result;
        }

        // 2. Inverse path (Base=Target, Quote=Source) e.g., USDT -> BTC (BTC/USDT)
        var inverseInst = _instrumentRepository.GetAll().FirstOrDefault(i =>
            i.BaseCurrency == target &&
            i.QuoteCurrency == source &&
            i.SourceExchange == ReferenceExchange &&
            i.ProductType == ReferenceProductType
        );

        if (inverseInst != null)
        {
            var result = (inverseInst, true);
            _conversionPathCache[key] = result;
            return result;
        }

        _conversionPathCache[key] = null;
        return null;
    }
}