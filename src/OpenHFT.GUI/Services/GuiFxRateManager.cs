using System;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;

namespace OpenHFT.GUI.Services;

public class GuiFxRateManager : FxRateManagerBase
{
    private readonly IOrderBookManager _orderBookManager;

    // Constructor updated: no longer needs List<FxRateConfig>
    public GuiFxRateManager(
        ILogger<GuiFxRateManager> logger,
        IInstrumentRepository instrumentRepository,
        IOrderBookManager orderBookManager)
        : base(logger, instrumentRepository) // Pass to base
    {
        _orderBookManager = orderBookManager;
    }

    protected override Price? GetConversionPrice(int instrumentId)
    {
        return _orderBookManager.GetOrderBook(instrumentId)?.GetMidPrice();
    }
}