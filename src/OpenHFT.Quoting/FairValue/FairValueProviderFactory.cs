using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Quoting.Interfaces;

namespace OpenHFT.Quoting.FairValue;

/// <summary>
/// A default factory for creating IFairValueProvider instances.
/// This factory uses an IServiceProvider to resolve dependencies (like loggers) 
/// for the providers it creates, following the Dependency Injection pattern.
/// </summary>
public class FairValueProviderFactory : IFairValueProviderFactory
{
    private readonly ILogger<FairValueProviderFactory> _logger;
    private readonly IInstrumentRepository _instrumentRepo;

    /// <summary>
    /// Initializes a new instance of the FairValueProviderFactory.
    /// </summary>
    public FairValueProviderFactory(ILogger<FairValueProviderFactory> logger, IInstrumentRepository instrumentRepo)
    {
        _logger = logger;
        _instrumentRepo = instrumentRepo;
    }

    /// <summary>
    /// Creates a new IFairValueProvider instance based on the specified model.
    /// </summary>
    /// <param name="model">The model type required for the provider (e.g., Midp, FR).</param>
    /// <param name="fvInstrumentId"></param>jjj
    /// <returns>A new, initialized Fair Value Provider.</returns>
    public IFairValueProvider CreateProvider(FairValueModel model, int fvInstrumentId)
    {
        switch (model)
        {
            case FairValueModel.Midp:
                // Resolve the specific logger needed for MidpFairValueProvider from the DI container.
                return new MidpFairValueProvider(_logger, fvInstrumentId);
            case FairValueModel.BestMidp:
                return new BestMidpFairValueProvider(_logger, fvInstrumentId);
            case FairValueModel.VwapMidp:
                return new VwapMidpFairValueProvider(_logger, fvInstrumentId);
            case FairValueModel.GroupedMidp:
                var instrument = _instrumentRepo.GetById(fvInstrumentId);
                if (instrument is null) throw new Exception($"Instrument with ID {fvInstrumentId} not found.");
                return new GroupedMidpFairValueProvider(_logger, fvInstrumentId, instrument.TickSize);
            case FairValueModel.FR:
                throw new NotImplementedException($"FairValueModel '{model}' is not yet implemented in FairValueProviderFactory.");
            default:
                // This handles any undefined enum values, ensuring robustness.
                throw new ArgumentOutOfRangeException(nameof(model), $"Unsupported FairValueModel: {model}. Please check if the enum value is correct and implemented in the factory.");
        }
    }
}