using System;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Gateway.ApiClient;

namespace OpenHFT.Gateway.Interfaces;

public interface IRestApiClientRegistry
{
    /// <summary>
    /// Gets the specialized REST API client for the given exchange and product type.
    /// </summary>
    /// <param name="exchange">The exchange (e.g., BITMEX).</param>
    /// <param name="productType">The product type (e.g., PerpetualFuture).</param>
    /// <returns>The required BaseRestApiClient instance.</returns>
    BaseRestApiClient GetClient(ExchangeEnum exchange, ProductType productType);
    IEnumerable<BaseRestApiClient> GetAllClients();
}