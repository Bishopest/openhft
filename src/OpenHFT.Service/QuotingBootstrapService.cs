using System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.Quoting;
using OpenHFT.Quoting.Interfaces;

namespace OpenHFT.Service;

public class QuotingBootstrapService : IHostedService
{
    private readonly ILogger<QuotingBootstrapService> _logger;
    private readonly IQuotingInstanceManager _instanceManager;
    private readonly IInstrumentRepository _instrumentRepository;
    private readonly QuoteDebugger _quoteDebugger;
    private readonly QuotingConfig[] _initialConfigs;

    public QuotingBootstrapService(
        ILogger<QuotingBootstrapService> logger,
        IQuotingInstanceManager instanceManager,
        IInstrumentRepository instrumentRepository,
        QuoteDebugger quoteDebugger,
        QuotingConfig[] initialConfigs) // 설정 배열 주입
    {
        _logger = logger;
        _instanceManager = instanceManager;
        _instrumentRepository = instrumentRepository;
        _quoteDebugger = quoteDebugger;
        _initialConfigs = initialConfigs;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller($"Bootstrapping quoting strategies from {_initialConfigs.Length} configurations...");

        foreach (var config in _initialConfigs)
        {
            if (!Enum.TryParse<ExchangeEnum>(config.Exchange, true, out var exchange) ||
                !Enum.TryParse<ProductType>(config.ProductType, true, out var productType))
            {
                _logger.LogWarningWithCaller($"Invalid config for {config.Symbol}. Skipping.");
                continue;
            }

            var instrument = _instrumentRepository.FindBySymbol(config.Symbol, productType, exchange);
            if (instrument == null)
            {
                _logger.LogWarningWithCaller($"Instrument '{config.Symbol}' not found. Skipping deployment.");
                continue;
            }

            if (!Enum.TryParse<ExchangeEnum>(config.FairValue.Params.Exchange, true, out var fvExchange) ||
                !Enum.TryParse<ProductType>(config.FairValue.Params.ProductType, true, out var fvProductType))
            {
                _logger.LogWarningWithCaller($"Invalid fair value params for {config.FairValue.Params.Symbol}. Skipping.");
                continue;
            }
            var sourceInstrument = _instrumentRepository.FindBySymbol(config.FairValue.Params.Symbol, fvProductType, fvExchange);

            if (sourceInstrument == null)
            {
                _logger.LogWarningWithCaller($"Source instrument '{config.FairValue.Params.Symbol}' not found. Skipping deployment.");
                continue;
            }

            var parameters = new QuotingParameters(
                instrument.InstrumentId,
                config.BookName,
                config.FairValue.Model,
                sourceInstrument.InstrumentId,
                config.AskSpreadBp,
                config.BidSpreadBp,
                config.SkewBp,
                Quantity.FromDecimal(config.Size),
                config.Depth,
                Enum.TryParse<QuoterType>(config.AskQuoterType, true, out var askQType) ? askQType : QuoterType.Log,
                Enum.TryParse<QuoterType>(config.BidQuoterType, true, out var bidQType) ? bidQType : QuoterType.Log,
                config.PostOnly,
                Quantity.FromDecimal(config.MaxCumBidFills),
                Quantity.FromDecimal(config.MaxCumAskFills),
                config.HittingLogic,
                config.GroupingBp
            );

            if (_instanceManager.UpdateInstanceParameters(parameters) != null)
            {
                _logger.LogInformationWithCaller($"Successfully deployed quoting strategy for {config.Symbol}.");
                _instanceManager.UpdateInstanceParameters(parameters);
            }
            else
            {
                _logger.LogWarningWithCaller($"Failed to deploy quoting strategy for {config.Symbol}.");
                continue;
            }
        }

        _logger.LogInformationWithCaller("All initial quoting strategies have been deployed.");
        _quoteDebugger.StartAsync(cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _quoteDebugger.StopAsync(cancellationToken);
        return Task.CompletedTask;
    }
}
