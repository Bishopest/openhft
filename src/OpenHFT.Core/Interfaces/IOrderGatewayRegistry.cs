using System;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Interfaces;

public interface IOrderGatewayRegistry
{
    IOrderGateway? GetGateway(ExchangeEnum exchange, ProductType productType);
    IOrderGateway? GetGatewayForInstrument(int instrumentId);

}
