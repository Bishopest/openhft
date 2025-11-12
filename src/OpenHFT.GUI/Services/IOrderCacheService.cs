using System;
using OpenHFT.Core.Models;

namespace OpenHFT.GUI.Services;

public interface IOrderCacheService
{
    event EventHandler<OrderStatusReport> OnOrdersUpdated;
    event EventHandler<Fill> OnFillsUpdated;

    IEnumerable<OrderStatusReport> GetActiveOrders(int instrumentId);
    IEnumerable<Fill> GetAllFills();

}
