using System;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;
using OpenHFT.Gateway.ApiClient;
using OpenHFT.Gateway.Interfaces;

namespace OpenHFT.Gateway;

public class RestApiClientRegistry : IRestApiClientRegistry
{
    private readonly IReadOnlyDictionary<(ExchangeEnum, ProductType), BaseRestApiClient> _clients;

    public RestApiClientRegistry(IEnumerable<BaseRestApiClient> clients)
    {
        _clients = clients.ToDictionary(c => (c.SourceExchange, c.ProdType));
    }

    public IEnumerable<BaseRestApiClient> GetAllClients()
    {
        return _clients.Values;
    }

    public BaseRestApiClient GetClient(ExchangeEnum exchange, ProductType productType)
    {
        if (_clients.TryGetValue((exchange, productType), out var client))
        {
            return client;
        }
        throw new KeyNotFoundException($"REST API Client for {exchange}/{productType} not found in the registry.");
    }
}
