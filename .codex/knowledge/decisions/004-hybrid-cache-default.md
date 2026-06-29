# ADR-004: HybridCache Over Manual IDistributedCache Patterns

## Status

Accepted

## Context

Caching is a cross-cutting concern in most .NET applications. Prior to .NET 9, the standard approach involved either `IMemoryCache` for in-process caching or `IDistributedCache` for distributed caching (Redis, SQL Server, etc.). Both approaches had significant drawbacks when used directly.

### The problem with manual IDistributedCache

The `IDistributedCache` interface requires the developer to handle serialization, null checks, and stampede protection manually:

```csharp
// The manual pattern -- error-prone and verbose
public async Task<Order?> GetOrderAsync(Guid id, CancellationToken ct)
{
    var cacheKey = $"order:{id}";
    var cached = await _cache.GetStringAsync(cacheKey, ct);

    if (cached is not null)
        return JsonSerializer.Deserialize<Order>(cached);

    var order = await _db.Orders.FindAsync([id], ct);

    if (order is not null)
    {
        var json = JsonSerializer.Serialize(order);
        await _cache.SetStringAsync(cacheKey, json,
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
            }, ct);
    }

    return order;
}
```

Problems with this pattern:

1. **Stampede vulnerability.** If 100 concurrent requests hit a cold cache, all 100 execute the database query simultaneously. There is no built-in locking or deduplication.
2. **Serialization boilerplate.** `IDistributedCache` works with `byte[]`. Every call site must serialize/deserialize manually.
3. **No L1+L2 composition.** Using both `IMemoryCache` (L1) and `IDistributedCache` (L2) requires manual orchestration: check L1, check L2, populate both, invalidate both.
4. **Error-prone cache key management.** Cache keys are strings with no structure or validation.
5. **Tag-based invalidation impossible.** Invalidating all cache entries for a given entity type (e.g., "all orders for customer X") requires tracking keys manually.

### The HybridCache solution

`HybridCache` (introduced in .NET 9 preview, GA in .NET 10) is Microsoft's answer to all of these problems. It provides:

- **Stampede protection** via internal locking/deduplication
- **Automatic L1 (in-memory) + L2 (distributed) layering**
- **Built-in serialization** (System.Text.Json by default, pluggable)
- **Tag-based invalidation**
- **A simple `GetOrCreateAsync` API** that eliminates boilerplate

## Decision

**HybridCache is the default caching abstraction in dotnet-claude-kit. Manual `IDistributedCache` patterns should be avoided for new code.**

### Default usage pattern

```csharp
// Registration
builder.Services.AddHybridCache(options =>
{
    options.DefaultEntryOptions = new HybridCacheEntryOptions
    {
        Expiration = TimeSpan.FromMinutes(10),
        LocalCacheExpiration = TimeSpan.FromMinutes(2)
    };
});

// Optionally add a distributed cache backend (Redis)
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("Redis");
});
```

```csharp
// Usage in a feature handler
public sealed class GetOrderHandler(HybridCache cache, AppDbContext db)
{
    public async Task<Result<OrderResponse>> HandleAsync(
        Guid orderId, CancellationToken ct)
    {
        var order = await cache.GetOrCreateAsync(
            $"order:{orderId}",
            async token => await db.Orders
                .AsNoTracking()
                .Where(o => o.Id == orderId)
                .Select(o => new OrderResponse(o.Id, o.Total, o.Status))
                .FirstOrDefaultAsync(token),
            tags: ["orders", $"order:{orderId}"],
            cancellationToken: ct);

        return order is not null
            ? Result.Success(order)
            : Result.Failure<OrderResponse>(Error.NotFound("Order", orderId));
    }
}
```

### Invalidation

```csharp
// Invalidate a specific entry
await cache.RemoveAsync($"order:{orderId}", ct);

// Invalidate all entries tagged with "orders"
await cache.RemoveByTagAsync("orders", ct);
```

### When NOT to use HybridCache

| Scenario | Recommended Approach |
|---|---|
| Simple in-memory caching with no distributed backend needed | `IMemoryCache` is sufficient |
| Output caching for entire HTTP responses | Use ASP.NET Core Output Caching middleware |
| Response caching via HTTP headers (CDN, browser) | Use `[ResponseCache]` or `Cache-Control` headers |
| Session state | Use ASP.NET Core session middleware |
| Fine-grained Redis data structures (sorted sets, streams, pub/sub) | Use `StackExchange.Redis` directly |

## Consequences

### Positive

- **Stampede protection out of the box.** If 100 concurrent requests ask for the same key, only one executes the factory function. The rest wait for and share the result.
- **Eliminated boilerplate.** A single `GetOrCreateAsync` call replaces 10-15 lines of manual cache-aside code. No manual serialization, no null checks, no expiration configuration per call (unless overridden).
- **L1+L2 composition is automatic.** HybridCache checks in-memory first (fast, no network), then distributed cache (fast, one network hop), then calls the factory. Both layers are populated automatically.
- **Tag-based invalidation.** Invalidating all cache entries for a logical group (e.g., all orders for a customer) is a single `RemoveByTagAsync` call instead of manually tracking and removing individual keys.
- **Consistent API.** The entire team uses the same caching API. No more inconsistencies between "someone used `IMemoryCache` here but `IDistributedCache` there."
- **AI-assisted development.** Claude generates correct caching code more reliably with HybridCache's simple API than with the manual `IDistributedCache` pattern.

### Negative

- **.NET 10+ only.** HybridCache is GA in .NET 10. Applications on .NET 8 LTS cannot use it without a preview package. (dotnet-claude-kit targets .NET 10, so this is acceptable.)
- **Less granular control.** For exotic caching scenarios (custom eviction policies, per-entry size limits), the abstraction may not expose the knobs you need.
- **Dependency on Microsoft's implementation.** If a bug exists in HybridCache, you are dependent on Microsoft for a fix, whereas a hand-rolled cache-aside pattern is fully within your control.
- **Learning curve.** Developers familiar with `IDistributedCache` must learn the new API (though it is simpler, not more complex).

### Mitigations

- The `caching` skill provides patterns for HybridCache, output caching, and guidance on when `IMemoryCache` alone is sufficient.
- The `common-antipatterns.md` knowledge document warns against manual `IDistributedCache` cache-aside patterns in new code.
- For applications stuck on .NET 8, the `caching` skill documents the manual pattern as a fallback with appropriate warnings about stampede vulnerability.
