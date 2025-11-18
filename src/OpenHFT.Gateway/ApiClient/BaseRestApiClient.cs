using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Api;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;

namespace OpenHFT.Gateway.ApiClient;

/// <summary>
/// A base class for creating REST API clients for cryptocurrency exchanges.
/// It handles common functionalities like HTTP requests, JSON serialization, and error handling.
/// This version focuses on public API endpoints and does not include request signing.
/// </summary>
public abstract class BaseRestApiClient : IDisposable
{
    protected readonly ILogger _logger;
    protected readonly IInstrumentRepository _instrumentRepository;
    protected readonly HttpClient _httpClient;
    public ProductType ProdType { get; }
    private bool _disposed;

    /// <summary>
    /// The API secret key.
    /// </summary>
    protected string? ApiSecret;

    /// <summary>
    /// The API key.
    /// </summary>
    protected string? ApiKey;

    /// <summary>
    /// Represents UserData Session listen key for WebSocket streams.
    /// </summary>
    public string? SessionId { get; protected set; }

    /// <summary>
    /// The base URL for the REST API. Must be implemented by derived classes.
    /// </summary>
    protected abstract string GetBaseUrl(ExecutionMode mode);
    protected readonly ExecutionMode _executionMode;
    public ExecutionMode ExecutionMode => _executionMode;
    public abstract ExchangeEnum SourceExchange { get; }

    protected BaseRestApiClient(
        ILogger logger,
        IInstrumentRepository instrumentRepository,
        HttpClient httpClient,
        ProductType productType,
        ExecutionMode executionMode,
        string? apiSecret = null,
        string? apiKey = null)
    {
        _logger = logger;
        _instrumentRepository = instrumentRepository;
        _httpClient = httpClient;
        ProdType = productType;
        _executionMode = executionMode;
        ApiSecret = apiSecret;
        ApiKey = apiKey;

        ConfigureHttpClient();
    }

    public abstract Task<long> GetServerTimeAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Configures the HttpClient instance, setting the base address and default headers.
    /// </summary>
    protected virtual void ConfigureHttpClient()
    {
        _httpClient.BaseAddress = new Uri(GetBaseUrl(_executionMode));
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Sends a public HTTP request and returns a result object, avoiding exceptions for predictable failures.
    /// </summary>
    /// <typeparam name="T">The type to deserialize the response into.</typeparam>
    /// <param name="method">The HTTP method (e.g., HttpMethod.Get).</param>
    /// <param name="endpoint">The API endpoint path (e.g., "/api/v3/ticker/price").</param>
    /// <param name="payload">The request body for POST/PUT requests. Will be serialized to JSON.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A RestApiResult object containing either the successful data or an error.</returns>
    public async Task<RestApiResult<T>> SendRequestAsync<T>(
        HttpMethod method,
        string endpoint,
        object? payload = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, endpoint.TrimStart('/'));

        if (payload != null)
        {
            try
            {
                var jsonPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to serialize payload for request to {Endpoint}.", endpoint);
                var error = new RestApiException($"Payload serialization error: {ex.Message}", ex);
                return RestApiResult<T>.Failure(error);
            }
        }

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            // 1. Handle non-successful HTTP status codes
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("API request to {Uri} failed with status {StatusCode}. Response: {Response}",
                    request.RequestUri, response.StatusCode, responseContent);
                var error = new RestApiException(
                    $"API request failed: {response.StatusCode}",
                    response.StatusCode,
                    responseContent);
                return RestApiResult<T>.Failure(error);
            }

            // 2. Handle empty responses
            if (string.IsNullOrWhiteSpace(responseContent))
            {
                var error = new RestApiException("API returned an empty response.", response.StatusCode, responseContent);
                return RestApiResult<T>.Failure(error);
            }

            // 3. Handle JSON parsing errors
            try
            {
                var result = JsonSerializer.Deserialize<T>(responseContent);
                if (result == null)
                {
                    var error = new RestApiException("Failed to deserialize API response to a non-null object.", response.StatusCode, responseContent);
                    return RestApiResult<T>.Failure(error);
                }

                // 4. Return success result
                return RestApiResult<T>.Success(result);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse JSON response from {Uri}. Response: {ResponseContent}", request.RequestUri, responseContent);
                var error = new RestApiException($"JSON parsing error: {ex.Message}", ex);
                return RestApiResult<T>.Failure(error);
            }
        }
        catch (HttpRequestException ex) // Handle network-level errors
        {
            _logger.LogError(ex, "HTTP request to {Uri} failed.", request.RequestUri);
            var error = new RestApiException($"Network error during API request: {ex.Message}", ex);
            return RestApiResult<T>.Failure(error);
        }
        catch (Exception ex) // Handle any other unexpected errors
        {
            _logger.LogError(ex, "An unexpected error occurred during the request to {Uri}.", request.RequestUri);
            var error = new RestApiException($"Request failed: {ex.Message}", ex);
            return RestApiResult<T>.Failure(error);
        }
    }

    /// <summary>
    /// Sends a signed, private HTTP request. This is a template method.
    /// </summary>
    public async Task<RestApiResult<T>> SendPrivateRequestAsync<T>(
        HttpMethod method,
        string endpoint,
        Dictionary<string, object>? queryParams = null,
        Dictionary<string, object>? bodyParams = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(ApiKey) || string.IsNullOrEmpty(ApiSecret))
            throw new InvalidOperationException("API credentials must be set.");

        // 1. creat query & body
        string queryString = queryParams != null ? BuildQueryString(queryParams) : string.Empty;
        string fullEndpoint = string.IsNullOrEmpty(queryString) ? endpoint : $"{endpoint}?{queryString}";

        using var request = new HttpRequestMessage(method, fullEndpoint.TrimStart('/'));

        // 2. add signature(abstract method) 
        AddSignatureToRequest(request, fullEndpoint, queryString, bodyParams);

        var queryForLog = queryParams != null ? JsonSerializer.Serialize(queryParams) : "N/A";
        var bodyForLog = bodyParams != null ? JsonSerializer.Serialize(bodyParams) : "N/A";
        // 3. send request & handle responses
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var ex = new RestApiException($"API Error: {response.StatusCode}", response.StatusCode, responseContent);
                // _logger.LogErrorWithCaller(ex, $"API request to {request.RequestUri} failed with status {response.StatusCode}. Response: {responseContent}");
                _logger.LogErrorWithCaller(ex,
                    $"API request to {request.RequestUri} failed with status {response.StatusCode}. Query: {queryForLog}, Body: {bodyForLog}, Response: {responseContent}");
                return RestApiResult<T>.Failure(ex);
            }
            try
            {
                var result = JsonSerializer.Deserialize<T>(responseContent);
                if (result == null)
                {
                    var ex = new RestApiException("Failed to deserialize response to a non-null object.", response.StatusCode, responseContent);
                    // _logger.LogErrorWithCaller(ex, $"Failed to deserialize API response from {request.RequestUri}. Response: {responseContent}");
                    _logger.LogErrorWithCaller(ex,
                        $"Failed to deserialize API response from {request.RequestUri}. Query: {queryForLog}, Body: {bodyForLog}, Response: {responseContent}");
                    return RestApiResult<T>.Failure(ex);
                }

                return RestApiResult<T>.Success(result);

            }
            catch (JsonException ex)
            {
                _logger.LogErrorWithCaller(ex,
                    $"Failed to parse JSON response from {request.RequestUri}. Query: {queryForLog}, Body: {bodyForLog}");
                var error = new RestApiException($"JSON parsing error: {ex.Message}", ex);
                return RestApiResult<T>.Failure(error);
            }
        }
        catch (Exception ex)
        {
            // _logger.LogErrorWithCaller(ex, $"An unexpected error occurred during the request to {request.RequestUri}.");
            _logger.LogErrorWithCaller(ex,
                $"An unexpected error occurred during the request to {request.RequestUri}. Query: {queryForLog}, Body: {bodyForLog}");
            var error = new RestApiException($"Request failed: {ex.Message}", ex);
            return RestApiResult<T>.Failure(error);
        }
    }

    /// <summary>
    /// Derived classes must implement this method to add their specific authentication
    /// headers or parameters to the request.
    /// </summary>
    protected abstract void AddSignatureToRequest(
        HttpRequestMessage request,
        string fullPath,
        string? queryString,
        Dictionary<string, object>? bodyParams);

    // Helper to build URL-encoded query strings
    protected string BuildQueryString(Dictionary<string, object> parameters)
    {
        return string.Join("&", parameters.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value.ToString())}"));
    }

    // Helper for HMAC-SHA256 signing
    protected string CreateHmacSha256Signature(string message)
    {
        var keyBytes = Encoding.UTF8.GetBytes(ApiSecret!);
        var messageBytes = Encoding.UTF8.GetBytes(message);
        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(messageBytes);
        return Convert.ToHexString(hash).ToLower();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            // HttpClient is typically managed by a Dependency Injection container,
            // so we don't dispose it here to avoid issues with its lifecycle.
        }

        _disposed = true;
    }
}
