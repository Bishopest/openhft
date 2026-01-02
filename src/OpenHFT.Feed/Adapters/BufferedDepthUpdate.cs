using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Feed.Adapters;

public struct BufferedDepthUpdate
{
    public long U;
    public long u;
    public long pu; // 선물용
    public long E;
    public PriceLevelEntry[] Entries; // ArrayPool에서 대여
    public int EntryCount;
}