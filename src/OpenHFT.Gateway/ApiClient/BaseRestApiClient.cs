using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Gateway.ApiClient.Exceptions;

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
    protected abstract string BaseUrl { get; }

    public abstract ExchangeEnum SourceExchange { get; }



    protected BaseRestApiClient(
        ILogger logger,
        IInstrumentRepository instrumentRepository,
        HttpClient httpClient,
        ProductType productType,
        string? apiSecret = null,
        string? apiKey = null)
    {
        _logger = logger;
        _instrumentRepository = instrumentRepository;
        _httpClient = httpClient;
        ProdType = productType;
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
        _httpClient.BaseAddress = new Uri(BaseUrl);
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Sends an HTTP request and deserializes the JSON response.
    /// </summary>
    /// <typeparam name="T">The type to deserialize the response into.</typeparam>
    /// <param name="method">The HTTP method (e.g., HttpMethod.Get).</param>
    /// <param name="endpoint">The API endpoint path (e.g., "/api/v3/ticker/price").</param>
    /// <param name="payload">The request body for POST/PUT requests. Will be serialized to JSON.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The deserialized response object.</returns>
    /// <exception cref="RestApiException">Thrown when the API returns an error or the request fails.</exception>
    public async Task<T> SendRequestAsync<T>(
        HttpMethod method,
        string endpoint,
        object? payload = null,
        CancellationToken cancellationToken = default)
    {
        // The BaseAddress is already set in ConfigureHttpClient. We only need the relative path.
        using var request = new HttpRequestMessage(method, endpoint.TrimStart('/'));

        if (payload != null)
        {
            var jsonPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions { IgnoreNullValues = true });
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        }

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("API request to {Uri} failed with status {StatusCode}. Response: {Response}",
                    request.RequestUri, response.StatusCode, responseContent);
                throw new RestApiException(
                    $"API request failed: {response.StatusCode}",
                    response.StatusCode,
                    responseContent);
            }

            if (string.IsNullOrWhiteSpace(responseContent))
            {
                throw new RestApiException("API returned an empty response.", response.StatusCode, responseContent);
            }

            var result = JsonSerializer.Deserialize<T>(responseContent);
            if (result == null)
            {
                throw new RestApiException("Failed to deserialize API response.", response.StatusCode, responseContent);
            }

            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP request to {Uri} failed.", request.RequestUri);
            throw new RestApiException($"Network error during API request: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse JSON response from {Uri}.", request.RequestUri);
            throw new RestApiException($"JSON parsing error: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Sends a signed, private HTTP request. This is a template method.
    /// </summary>
    public async Task<T> SendPrivateRequestAsync<T>(
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
        AddSignatureToRequest(request, queryString, bodyParams);

        // 3. send request & handle responses
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new RestApiException($"API Error: {response.StatusCode}", response.StatusCode, responseContent);
            }

            var result = JsonSerializer.Deserialize<T>(responseContent);
            if (result == null) throw new RestApiException("Failed to deserialize response.", response.StatusCode, responseContent);

            return result;
        }
        catch (Exception ex)
        {
            throw new RestApiException($"Request failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Derived classes must implement this method to add their specific authentication
    /// headers or parameters to the request.
    /// </summary>
    protected abstract void AddSignatureToRequest(
        HttpRequestMessage request,
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
