using System;

namespace OpenHFT.Quoting.Interfaces;

public interface IQuotingInstanceFactory
{
    QuotingInstance? Create(QuotingParameters parameters);

}
