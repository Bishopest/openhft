using System;
using OpenHFT.Oms.Api.WebSocket;

namespace OpenHFT.GUI.Services;

public interface IHedgingCacheService
{
    event Action? OnHedgingStatusUpdated;
    HedgingStatusPayload? GetHedgingStatus(string omsIdentifier, int quotingInstrumentId);
    void ClearCacheForOms(string omsIdentifier);
}
