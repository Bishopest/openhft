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

    protected override Price? GetMidPrice(int instrumentId)
    {
        return _marketDataManager.GetOrderBook(instrumentId)?.GetMidPrice();
    }
}
