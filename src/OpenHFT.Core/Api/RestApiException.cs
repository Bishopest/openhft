using System;
using System.Net;

namespace OpenHFT.Core.Api;

/// <summary>
/// Represents errors that occur during REST API requests.
/// </summary>
public class RestApiException : Exception
{
    /// <summary>
    /// The HTTP status code of the response, if available.
    /// </summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// The raw response content from the API, if available.
    /// </summary>
    public string? ResponseContent { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="RestApiException"/> class.
    /// </summary>
    public RestApiException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="RestApiException"/> class with a specified error message and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    public RestApiException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="RestApiException"/> class with a specified error message, HTTP status code, and response content.
    /// </summary>
    public RestApiException(string message, HttpStatusCode statusCode, string? responseContent, Exception? innerException = null) : base(message, innerException)
    {
        StatusCode = statusCode;
        ResponseContent = responseContent;
    }
}