using System;

namespace OpenHFT.Core.Api;

/// <summary>
/// Encapsulates the result of a REST API call, indicating success or failure.
/// </summary>
/// <typeparam name="T">The type of the successful data payload.</typeparam>
public readonly struct RestApiResult<T>
{
    public readonly bool IsSuccess { get; }

    // These are only valid if IsSuccess is true/false respectively.
    private readonly T? _data;
    private readonly RestApiException? _error;

    // Private constructor to enforce factory method usage.
    private RestApiResult(bool isSuccess, T? data, RestApiException? error)
    {
        IsSuccess = isSuccess;
        _data = data;
        _error = error;
    }

    /// <summary>
    /// Gets the data from a successful result. Throws if the result was a failure.
    /// </summary>
    public T Data => IsSuccess ? _data! : throw new InvalidOperationException("Result is not successful.");

    /// <summary>
    /// Gets the error from a failed result. Throws if the result was a success.
    /// </summary>
    public RestApiException Error => !IsSuccess ? _error! : throw new InvalidOperationException("Result is successful.");

    // Factory method for creating a success result.
    public static RestApiResult<T> Success(T data)
    {
        return new RestApiResult<T>(true, data, null);
    }

    // Factory method for creating a failure result.
    public static RestApiResult<T> Failure(RestApiException error)
    {
        return new RestApiResult<T>(false, default, error);
    }
}