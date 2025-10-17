using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Quoting.Orders.Interfaces;

// This is a sample factory interface. In a real DI container,
// you might register Func<IOrder> or a more complex factory.
public interface IOrderFactory
{
    // The factory needs the basic, immutable properties to create the correct order type.
    IOrder Create(int instrumentId, Side side);
}
