using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Feed.Interfaces;

namespace OpenHFT.Feed.Adapters;

/// <summary>
/// An abstract base class for feed adapters that require API key authentication for private channels.
/// </summary>
public abstract class BaseAuthFeedAdapter : BaseFeedAdapter
{
    protected string? ApiKey { get; private set; }
    protected string? ApiSecret { get; private set; }
    public event EventHandler<AuthenticationEventArgs>? AuthenticationStateChanged;
    protected BaseAuthFeedAdapter(ILogger logger, ProductType type, IInstrumentRepository instrumentRepository)
        : base(logger, type, instrumentRepository)
    {
    }

    public override async Task AuthenticateAsync(string apiKey, string apiSecret, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
            throw new ArgumentException("API key and secret cannot be null or empty for authentication.");

        ApiKey = apiKey;
        ApiSecret = apiSecret;

        // Delegate the exchange-specific authentication logic to the derived class.
        try
        {
            await DoAuthenticateAsync(cancellationToken);
            AuthenticationStateChanged?.Invoke(this, new AuthenticationEventArgs(true, "Authentication successful."));
        }
        catch (Exception ex)
        {
            AuthenticationStateChanged?.Invoke(this, new AuthenticationEventArgs(false, $"Authentication failed: {ex.Message}"));
            throw;
        }
    }

    /// <summary>
    /// Derived classes must implement this method with their specific authentication payload and logic.
    /// </summary>
    protected abstract Task DoAuthenticateAsync(CancellationToken cancellationToken);

    public abstract Task SubscribeToPrivateTopicsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Helper method to create a HMAC-SHA256 signature, common to many exchanges.
    /// </summary>
    protected string CreateSignature(string message)
    {
        if (ApiSecret is null)
            throw new InvalidOperationException("API secret is not set. Call AuthenticateAsync first.");

        var keyBytes = Encoding.UTF8.GetBytes(ApiSecret);
        var messageBytes = Encoding.UTF8.GetBytes(message);
        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(messageBytes);

        // Convert byte array to a lowercase hex string
        return Convert.ToHexString(hash).ToLower();
    }
}
