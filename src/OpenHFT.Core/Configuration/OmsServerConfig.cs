using System;

namespace OpenHFT.Core.Configuration;

public class OmsServerConfig
{
    public required string OmsIdentifier
    {
        get; set;
    }
    public required string Url
    {
        get; set;

    }
}
