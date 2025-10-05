using System;
using OpenHFT.Core.Models;

namespace OpenHFT.Feed.Exceptions;

public class FeedException : Exception
{
    public ExchangeEnum SourceExchange { get; }

    public FeedException(ExchangeEnum exchange, string? message) : base(message)
    {
        SourceExchange = exchange;
    }

    public FeedException(ExchangeEnum exchange, string? message, Exception? innerException) : base(message, innerException)
    {
        SourceExchange = exchange;
    }
}

public class FeedConnectionException : FeedException
{
    public Uri ConnectUri { get; }

    public FeedConnectionException(ExchangeEnum exchange, Uri connectUri, string? message)
        : base(exchange, message)
    {
        ConnectUri = connectUri;
    }

    public FeedConnectionException(ExchangeEnum exchange, Uri connectUri, string? message, Exception? innerException)
        : base(exchange, message, innerException)
    {
        ConnectUri = connectUri;
    }
}

public class FeedReceiveException : FeedException
{
    public FeedReceiveException(ExchangeEnum exchange, string? message, Exception? innerException)
        : base(exchange, message, innerException) { }
}

public class FeedParseException : FeedException
{
    public string RawMessage { get; }

    public int TopicId { get; }

    public FeedParseException(ExchangeEnum exchange, string rawMessage, string? message, Exception? innerException, int topicId = 0)
        : base(exchange, message, innerException)
    {
        RawMessage = rawMessage;
        TopicId = topicId;
    }
}