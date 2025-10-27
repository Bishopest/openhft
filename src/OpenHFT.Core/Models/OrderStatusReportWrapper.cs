using System;

namespace OpenHFT.Core.Models;

public class OrderStatusReportWrapper
{
    public OrderStatusReport Report { get; private set; }

    public void SetData(in OrderStatusReport report)
    {
        Report = report;
    }
}