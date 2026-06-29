# What's New in .NET 10 and C# 14

> Last updated: February 2026 — .NET 10 release

## C# 14 Language Features

### Extension Members
Extension methods evolve into full extension members — properties, static methods, and more.

```csharp
public extension StringExtensions for string
{
    public bool IsNullOrEmpty => string.IsNullOrEmpty(this);
    public string Truncate(int maxLength) => Length <= maxLength ? this : this[..maxLength] + "...";
}
```

### The `field` Keyword
Access the compiler-generated backing field in property accessors.

```csharp
public string Name
{
    get => field;
    set => field = value?.Trim() ?? throw new ArgumentNullException(nameof(value));
}
```

### `allows ref struct` in Generics
Generic type parameters can now accept `ref struct` types, enabling `Span<T>` in generic contexts.

```csharp
public T Parse<T>(ReadOnlySpan<char> input) where T : ISpanParsable<T>
    => T.Parse(input, null);
```

### `params` Collections
`params` now works with any collection type, not just arrays.

```csharp
public void Log(params ReadOnlySpan<string> messages) { }
public void Add(params IEnumerable<int> values) { }
```

### Improved Pattern Matching
Extended property patterns and dictionary patterns.

### Partial Properties and Indexers
Source generators can now use partial properties.

## .NET 10 Runtime & Libraries

### ASP.NET Core
- **Built-in OpenAPI** — `builder.Services.AddOpenApi()` replaces Swashbuckle for most scenarios
- **Improved minimal API binding** — Better `[AsParameters]` support, complex type binding
- **HybridCache GA** — Graduated from preview; the default caching abstraction
- **Blazor enhancements** — Improved SSR, navigation, enhanced form handling

### Entity Framework Core 10
- **LINQ improvements** — Better query translation for complex expressions
- **ExecuteUpdateAsync/ExecuteDeleteAsync** refinements
- **Improved migrations** — Better handling of complex schema changes
- **Enhanced value converters** — Less ceremony for common patterns

### BCL (Base Class Library)
- **`TimeProvider`** — Standard abstraction for time (GA since .NET 8, now widely adopted)
- **`FrozenDictionary<K,V>` / `FrozenSet<T>`** — Immutable collections optimized for read-heavy workloads
- **`SearchValues<T>`** — Optimized searching within sets of values
- **`CompositeFormat`** — Pre-parsed format strings for high-perf logging
- **`Lock` type** — Dedicated lock type replacing `object` locks (since .NET 9)

### Performance
- **Dynamic PGO on by default** — Profile-guided optimization automatically tunes hot paths
- **Native AOT improvements** — Better trimming, more libraries compatible
- **Arm64 optimizations** — Continued investment in ARM performance

### Container & Deployment
- **Smaller container images** — `mcr.microsoft.com/dotnet/nightly/aspnet:10.0` with reduced layers
- **Non-root by default** — Container images run as non-root user
- **.NET Aspire 9+** — Improved orchestration, dashboard, and service discovery

## Key NuGet Ecosystem Updates

| Package | Version | Notable Changes |
|---------|---------|----------------|
| MediatR | 13.x | Improved performance, better DI integration |
| FluentValidation | 12.x | .NET 10 support, better minimal API integration |
| Serilog | 4.x | `LoggerConfiguration` improvements |
| MassTransit | 9.x | Better Aspire integration, improved outbox |
| NUnit | 4.x | Modern NUnit assertions and adapter support |
| Testcontainers | 4.x | Faster startup, more container modules |
| Polly | 8.x | Resilience pipelines (replaces policies) |

## Migration Notes (.NET 9 to .NET 10)

1. Update `TargetFramework` to `net10.0`
2. Update `LangVersion` to `14` (or `latest`)
3. Review `breaking-changes.md` for specific breaking changes
4. HybridCache is now GA — migrate from manual `IDistributedCache` patterns
5. Built-in OpenAPI may replace your Swashbuckle dependency
