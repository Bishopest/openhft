using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.Interfaces;

public interface IOrderSettable
{
    public Price Price { set; }
    public Quantity Quantity { set; }
    public Quantity LeavesQuantity { set; }
    public OrderType OrderType { set; }
    public bool IsPostOnly { set; }

    // 이벤트 핸들러 추가 메서드도 여기에 정의 가능
    void AddStatusChangedHandler(EventHandler<OrderStatusReport> handler);
}
