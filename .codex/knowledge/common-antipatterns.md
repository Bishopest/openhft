# Common Anti-patterns

> Patterns that Claude tends to generate incorrectly. Every developer using dotnet-claude-kit should be protected from these mistakes.

## async void

**Problem:** `async void` methods swallow exceptions and cannot be awaited. They're only valid for event handlers.

```csharp
// BAD — exception will crash the process or be silently swallowed
public async void ProcessOrder(Order order)
{
    await _repository.SaveAsync(order);
}

// GOOD — always return Task
public async Task ProcessOrderAsync(Order order)
{
    await _repository.SaveAsync(order);
}
```

## Task.Result / Task.Wait()

**Problem:** Synchronously blocking on async code causes deadlocks in ASP.NET and UI contexts.

```csharp
// BAD — deadlock risk
var order = _orderService.GetOrderAsync(id).Result;
_orderService.GetOrderAsync(id).Wait();
var order = _orderService.GetOrderAsync(id).GetAwaiter().GetResult();

// GOOD — await all the way
var order = await _orderService.GetOrderAsync(id);
```

## HttpClient Without IHttpClientFactory

**Problem:** Creating `HttpClient` directly causes socket exhaustion and DNS caching issues.

```csharp
// BAD — socket exhaustion
public class PaymentService
{
    public async Task<bool> ChargeAsync(decimal amount)
    {
        using var client = new HttpClient(); // DON'T
        var response = await client.PostAsync("https://payments.example.com/charge", ...);
        return response.IsSuccessStatusCode;
    }
}

// GOOD — IHttpClientFactory via named or typed clients
builder.Services.AddHttpClient<PaymentService>(client =>
{
    client.BaseAddress = new Uri("https://payments.example.com");
    client.Timeout = TimeSpan.FromSeconds(30);
});

public class PaymentService(HttpClient client)
{
    public async Task<bool> ChargeAsync(decimal amount)
    {
        var response = await client.PostAsync("/charge", ...);
        return response.IsSuccessStatusCode;
    }
}
```

## DateTime.Now / DateTime.UtcNow

**Problem:** Direct use of `DateTime.Now` makes code untestable and can cause timezone bugs.

```csharp
// BAD — untestable, timezone-dependent
public class OrderService
{
    public Order CreateOrder() => new() { CreatedAt = DateTime.Now };
}

// GOOD — inject TimeProvider
public class OrderService(TimeProvider clock)
{
    public Order CreateOrder() => new() { CreatedAt = clock.GetUtcNow() };
}

// In tests, use FakeTimeProvider
var clock = new FakeTimeProvider(new DateTimeOffset(2025, 6, 15, 0, 0, 0, TimeSpan.Zero));
```

## Throwing Exceptions for Flow Control

**Problem:** Exceptions are expensive (stack trace capture) and make control flow hard to follow. Use the Result pattern instead.

```csharp
// BAD — exceptions for expected failures
public Order GetOrder(Guid id)
{
    var order = _db.Orders.Find(id);
    if (order is null)
        throw new NotFoundException($"Order {id} not found"); // Expected case, not exceptional
    return order;
}

// GOOD — Result pattern
public Result<Order> GetOrder(Guid id)
{
    var order = _db.Orders.Find(id);
    return order is not null
        ? Result.Success(order)
        : Result.Failure<Order>($"Order {id} not found");
}
```

## Catching System.Exception

**Problem:** Catching the base `Exception` type hides bugs and swallows critical errors.

```csharp
// BAD — catches everything including OutOfMemoryException
try
{
    await ProcessOrder(order);
}
catch (Exception ex)
{
    _logger.LogError(ex, "Something went wrong");
    return Results.Problem("An error occurred");
}

// GOOD — catch specific exceptions
try
{
    await ProcessOrder(order);
}
catch (PaymentDeclinedException ex)
{
    _logger.LogWarning(ex, "Payment declined for order {OrderId}", order.Id);
    return Results.Problem("Payment was declined", statusCode: 402);
}
// Let unexpected exceptions propagate to the global handler
```

## Service Locator Pattern

**Problem:** Resolving services from `IServiceProvider` directly hides dependencies and breaks compile-time checking.

```csharp
// BAD — service locator
public class OrderService(IServiceProvider provider)
{
    public async Task Process()
    {
        var repo = provider.GetRequiredService<IOrderRepository>();
        var logger = provider.GetRequiredService<ILogger<OrderService>>();
    }
}

// GOOD — explicit constructor injection
public class OrderService(IOrderRepository repository, ILogger<OrderService> logger)
{
    public async Task Process() { /* use repository and logger directly */ }
}
```

## String Concatenation in Logging

**Problem:** String interpolation in log messages prevents structured logging and wastes allocations when the log level is disabled.

```csharp
// BAD — allocates string even if Information level is disabled
_logger.LogInformation($"Processing order {orderId} for customer {customerId}");

// BAD — same problem with string.Format
_logger.LogInformation(string.Format("Processing order {0}", orderId));

// GOOD — structured logging with message template
_logger.LogInformation("Processing order {OrderId} for customer {CustomerId}", orderId, customerId);
```

## Disposing IServiceScope Incorrectly

**Problem:** Creating a scope but not disposing it leaks scoped services.

```csharp
// BAD — scope never disposed
var scope = serviceProvider.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

// GOOD — using statement
using var scope = serviceProvider.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
await db.Orders.ToListAsync();

// GOOD — async using in async context
await using var scope = serviceProvider.CreateAsyncScope();
```

## EF Core: Tracking Queries for Read-Only Operations

**Problem:** Change tracking adds overhead for queries that only read data.

```csharp
// BAD — tracking enabled for a read-only list endpoint
var orders = await db.Orders
    .Include(o => o.Items)
    .ToListAsync(ct);

// GOOD — disable tracking for read operations
var orders = await db.Orders
    .AsNoTracking()
    .Include(o => o.Items)
    .ToListAsync(ct);

// EVEN BETTER — project to DTO (no tracking needed)
var orders = await db.Orders
    .Select(o => new OrderSummary(o.Id, o.Total, o.Status))
    .ToListAsync(ct);
```

## Registering Scoped Services as Singletons

**Problem:** Capturing a scoped service (like `DbContext`) in a singleton causes it to live forever, leading to stale data and memory leaks.

```csharp
// BAD — DbContext captured in singleton
builder.Services.AddSingleton<OrderCache>(); // OrderCache depends on AppDbContext

// GOOD — match lifetimes, or use IServiceScopeFactory
builder.Services.AddScoped<OrderCache>();

// Or if singleton is required:
public class OrderCache(IServiceScopeFactory scopeFactory)
{
    public async Task<Order?> GetAsync(Guid id)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Orders.FindAsync(id);
    }
}
```

## Not Passing CancellationToken

**Problem:** Without `CancellationToken`, requests continue processing after the client disconnects, wasting server resources.

```csharp
// BAD — no cancellation support
public async Task<List<Order>> GetOrdersAsync()
{
    return await db.Orders.ToListAsync();
}

// GOOD — propagate CancellationToken
public async Task<List<Order>> GetOrdersAsync(CancellationToken ct)
{
    return await db.Orders.ToListAsync(ct);
}
```
