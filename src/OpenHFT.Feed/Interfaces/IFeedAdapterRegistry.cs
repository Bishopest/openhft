using System;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;

namespace OpenHFT.Feed.Interfaces;

public interface IFeedAdapterRegistry
{
    IFeedAdapter? GetAdapter(ExchangeEnum exchange, ProductType productType, StreamType streamType = StreamType.PublicStream);
    IEnumerable<IFeedAdapter> GetAllAdapters();
}