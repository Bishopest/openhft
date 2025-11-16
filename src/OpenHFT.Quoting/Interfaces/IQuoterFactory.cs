using System;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Models;

namespace OpenHFT.Quoting.Interfaces;

public interface IQuoterFactory
{
    /// <summary>
    /// Creates a quoter of the specified type for the given instrument and side.
    /// </summary>
    IQuoter CreateQuoter(Instrument instrument, Side side, QuoterType type);

}
