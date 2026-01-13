using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Utils;

namespace OpenHFT.Core.Models; // Assuming this is the correct namespace

// Internal record to define a conversion instrument.

public abstract class FxRateManagerBase : IFxRateService
{
    // --- HARDCODED CONFIGURATION ---
    // All supported FX conversion instruments are defined here.
    // To add a new path (e.g., ETH/USDT), simply add a new line.
    private record FxConversionInstrument(Currency Base, Currency Quote, ExchangeEnum Exchange, ProductType ProductType);
    private static readonly List<FxConversionInstrument> SupportedInstruments = new()
    {
        // Path for BTC <-> USDT conversion
        new(Currency.BTC, Currency.USDT, ExchangeEnum.BINANCE, ProductType.PerpetualFuture),
        
        // Path for KRW <-> USDT conversion
        new(Currency.USDT, Currency.KRW, ExchangeEnum.BITHUMB, ProductType.Spot),
    };

    protected readonly ILogger<FxRateManagerBase> _logger;
    protected readonly IInstrumentRepository _instrumentRepository;

    private readonly ConcurrentDictionary<(Currency, Currency), List<(Instrument Instrument, bool IsInverted)>> _pathCache = new();

    protected FxRateManagerBase(ILogger<FxRateManagerBase> logger, IInstrumentRepository instrumentRepository)
    {
        _logger = logger;
        _instrumentRepository = instrumentRepository;
    }

    protected abstract Price? GetMidPrice(int instrumentId);

    public CurrencyAmount? Convert(CurrencyAmount source, Currency target)
    {
        if (source.Currency == target) return source;

        var path = FindConversionPath(source.Currency, target);
        if (path == null || !path.Any())
        {
            _logger.LogWarningWithCaller($"No conversion path found for {source.Currency} -> {target}");
            return null;
        }

        // The rest of the Convert logic is the same as the previous suggestion.
        decimal currentAmount = source.Amount;
        foreach (var (instrument, isInverted) in path)
        {
            var midPrice = GetMidPrice(instrument.InstrumentId);
            if (midPrice == null || midPrice.Value.ToDecimal() <= 0)
            {
                _logger.LogWarningWithCaller($"Price not available for {instrument.Symbol} during conversion.");
                return null;
            }
            currentAmount = isInverted
                ? currentAmount / midPrice.Value.ToDecimal()
                : currentAmount * midPrice.Value.ToDecimal();
        }
        return new CurrencyAmount(currentAmount, target);
    }

    private List<(Instrument, bool)>? FindConversionPath(Currency source, Currency target)
    {
        if (_pathCache.TryGetValue((source, target), out var cachedPath)) return cachedPath;

        var queue = new Queue<(Currency currency, List<(Instrument, bool)> path)>();
        queue.Enqueue((source, new List<(Instrument, bool)>()));
        var visited = new HashSet<Currency> { source };

        while (queue.Any())
        {
            var (current, path) = queue.Dequeue();
            if (current == target)
            {
                _pathCache.TryAdd((source, target), path);
                return path;
            }

            if (path.Count >= 2) continue; // Limit search depth

            foreach (var fxInst in SupportedInstruments)
            {
                if (fxInst.Base == current && !visited.Contains(fxInst.Quote))
                {
                    var inst = _instrumentRepository.GetAll().FirstOrDefault(i =>
                        i.BaseCurrency == fxInst.Base && i.QuoteCurrency == fxInst.Quote &&
                        i.SourceExchange == fxInst.Exchange && i.ProductType == fxInst.ProductType);

                    if (inst != null)
                    {
                        var newPath = new List<(Instrument, bool)>(path) { (inst, false) };
                        queue.Enqueue((fxInst.Quote, newPath));
                        visited.Add(fxInst.Quote);
                    }
                }
                else if (fxInst.Quote == current && !visited.Contains(fxInst.Base))
                {
                    var inst = _instrumentRepository.GetAll().FirstOrDefault(i =>
                       i.BaseCurrency == fxInst.Base && i.QuoteCurrency == fxInst.Quote &&
                       i.SourceExchange == fxInst.Exchange && i.ProductType == fxInst.ProductType);

                    if (inst != null)
                    {
                        var newPath = new List<(Instrument, bool)>(path) { (inst, true) };
                        queue.Enqueue((fxInst.Base, newPath));
                        visited.Add(fxInst.Base);
                    }
                }
            }
        }

        _pathCache.TryAdd((source, target), null);
        return null;
    }

    /// <summary>
    /// Gets all unique instruments required for FX rate conversions.
    /// This allows other services like SubscriptionManager to know which instruments to subscribe to at startup.
    /// </summary>
    public static IEnumerable<(ExchangeEnum Exchange, ProductType ProductType, Currency Base, Currency Quote)> GetRequiredFxInstruments()
    {
        return SupportedInstruments
            .Select(inst => (inst.Exchange, inst.ProductType, inst.Base, inst.Quote))
            .Distinct();
    }
}