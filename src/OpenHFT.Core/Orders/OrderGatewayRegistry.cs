using System;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Orders;

public class OrderGatewayRegistry : IOrderGatewayRegistry
{
    private readonly IReadOnlyDictionary<(ExchangeEnum, ProductType), IOrderGateway> _gateways;
    private readonly IInstrumentRepository _instrumentRepository;

    public OrderGatewayRegistry(
        IEnumerable<IOrderGateway> gateways,
        IInstrumentRepository instrumentRepository)
    {
        _gateways = gateways.ToDictionary(g => (g.SourceExchange, g.ProdType), g => g);
        _instrumentRepository = instrumentRepository;
    }

    public IOrderGateway? GetGateway(ExchangeEnum exchange, ProductType productType)
    {
        if (_gateways.TryGetValue((exchange, productType), out var gateway))
        {
            return gateway;
        }

        return null;
    }

    public IOrderGateway? GetGatewayForInstrument(int instrumentId)
    {
        var instrument = _instrumentRepository.GetById(instrumentId);
        if (instrument == null)
        {
            return null;
        }

        return GetGateway(instrument.SourceExchange, instrument.ProductType);
    }
}
