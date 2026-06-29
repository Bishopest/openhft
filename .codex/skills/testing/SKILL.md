---
name: testing
description: >
  Testing strategy for .NET 10 applications. Covers NUnit, WebApplicationFactory
  for integration tests, Testcontainers for real database testing, Verify for
  snapshot testing, and the AAA pattern.
  Load this skill when writing tests, setting up test infrastructure, reviewing
  test coverage, or when the user mentions "test", "NUnit", "WebApplicationFactory",
  "Testcontainers", "integration test", "unit test", "bUnit", "snapshot test",
  "Verify", "test coverage", "AAA pattern", "WireMock", or "FakeTimeProvider".
---

# Testing (.NET 10)

## Core Principles

1. **Integration tests are the highest-value tests** — A single `WebApplicationFactory` test covers routing, binding, validation, business logic, and persistence in one shot. Start here before writing unit tests.
2. **Real databases in tests** — Use Testcontainers to spin up real PostgreSQL/SQL Server instances. In-memory providers hide real bugs (transactions, constraints, SQL generation).
3. **AAA pattern is mandatory** — Every test has three clearly separated sections: Arrange, Act, Assert. No mixing.
4. **Test behavior, not implementation** — Tests should survive refactoring. Test what the system does, not how it does it.

## Patterns

### NUnit Basics

```csharp
[TestFixture]
public class OrderServiceTests
{
    [Test]
    public async Task CreateOrder_WithValidItems_ReturnsSuccessResult()
    {
        // Arrange
        var db = CreateInMemoryDb();
        var clock = new FakeTimeProvider(new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero));
        var service = new OrderService(db, clock);
        var request = new CreateOrderRequest("customer-1", [new("product-1", 2)]);

        // Act
        var result = await service.CreateAsync(request);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value.Id, Is.Not.EqualTo(Guid.Empty));
        Assert.That(result.Value.CreatedAt, Is.EqualTo(clock.GetUtcNow()));
    }

    [TestCase("")]
    [TestCase(null)]
    public async Task CreateOrder_WithInvalidCustomerId_ReturnsFailure(string? customerId)
    {
        // Arrange
        var service = CreateService();

        // Act
        var result = await service.CreateAsync(new CreateOrderRequest(customerId!, []));

        // Assert
        Assert.That(result.IsSuccess, Is.False);
    }
}
```

### Integration Tests with WebApplicationFactory

The highest-value test pattern. Tests the full HTTP pipeline.

```csharp
// Fixtures/ApiFixture.cs
public class ApiFixture : WebApplicationFactory<Program>
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17")
        .Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Replace the real DB with Testcontainers
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options =>
                options.UseNpgsql(_postgres.GetConnectionString()));
        });
    }

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await _postgres.StartAsync();

        // Apply migrations
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }
}
```

```csharp
// Tests/Orders/CreateOrderTests.cs
public class CreateOrderTests
{
    private ApiFixture _fixture = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = new ApiFixture();
        await _fixture.OneTimeSetUp();
        _client = _fixture.CreateClient();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        _client.Dispose();
        await _fixture.OneTimeTearDown();
    }

    [Test]
    public async Task CreateOrder_ReturnsCreated_WithValidRequest()
    {
        // Arrange
        var request = new CreateOrderRequest("customer-1", [new("product-1", 2)]);

        // Act
        var response = await _client.PostAsJsonAsync("/api/orders", request);

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        var order = await response.Content.ReadFromJsonAsync<OrderResponse>();
        Assert.That(order, Is.Not.Null);
        Assert.That(order!.Id, Is.Not.EqualTo(Guid.Empty));
        Assert.That(response.Headers.Location?.ToString(), Does.Contain("/api/orders/"));
    }

    [Test]
    public async Task CreateOrder_ReturnsValidationProblem_WithEmptyItems()
    {
        // Arrange
        var request = new CreateOrderRequest("customer-1", []);

        // Act
        var response = await _client.PostAsJsonAsync("/api/orders", request);

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
}
```

### Testcontainers for Real Database Testing

```csharp
// For SQL Server
private readonly MsSqlContainer _mssql = new MsSqlBuilder()
    .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
    .Build();

// For PostgreSQL
private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
    .WithImage("postgres:17")
    .Build();

// For Redis
private readonly RedisContainer _redis = new RedisBuilder()
    .WithImage("redis:7")
    .Build();
```

### Verify Snapshot Testing

Use Verify for complex response objects where manual assertions would be fragile.

```csharp
[Test]
public async Task GetOrder_MatchesSnapshot()
{
    // Arrange
    await SeedOrder(fixture);

    // Act
    var response = await _client.GetAsync("/api/orders/known-id");
    var content = await response.Content.ReadAsStringAsync();

    // Assert — compares against a stored .verified.txt file
    await Verify(content);
}
```

On first run, Verify creates a `.verified.txt` file. On subsequent runs, it compares output. If the output changes, the test fails and shows a diff.

### Test Data Builders

```csharp
public class OrderBuilder
{
    private string _customerId = "default-customer";
    private List<OrderItem> _items = [new("product-1", 1, 9.99m)];
    private OrderStatus _status = OrderStatus.Pending;

    public OrderBuilder WithCustomer(string customerId)
    {
        _customerId = customerId;
        return this;
    }

    public OrderBuilder WithItems(params OrderItem[] items)
    {
        _items = [..items];
        return this;
    }

    public OrderBuilder WithStatus(OrderStatus status)
    {
        _status = status;
        return this;
    }

    public Order Build() => Order.Create(_customerId, _items, _status);
}

// Usage in tests
var order = new OrderBuilder()
    .WithCustomer("vip-customer")
    .WithStatus(OrderStatus.Confirmed)
    .Build();
```

### Testing Time-Dependent Code

Use `TimeProvider` (built into .NET 8+) and `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing`.

```csharp
[Test]
public async Task ExpireOrders_MarksOldPendingOrdersAsExpired()
{
    // Arrange
    var clock = new FakeTimeProvider(new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero));
    var db = CreateDb();
    var order = Order.Create("customer-1", items, clock.GetUtcNow());
    db.Orders.Add(order);
    await db.SaveChangesAsync();

    // Advance time past expiry threshold
    clock.Advance(TimeSpan.FromDays(31));

    var handler = new ExpireOrders.Handler(db, clock);

    // Act
    await handler.Handle(new ExpireOrders.Command(), CancellationToken.None);

    // Assert
    var updated = await db.Orders.FindAsync(order.Id);
    Assert.That(updated!.Status, Is.EqualTo(OrderStatus.Expired));
}
```

### Test Naming Convention

Use the pattern: `MethodName_StateUnderTest_ExpectedBehavior`

```csharp
[Test] public async Task CreateOrder_WithValidItems_ReturnsSuccessResult() { }
[Test] public async Task CreateOrder_WithEmptyItems_ReturnsValidationError() { }
[Test] public async Task GetOrder_WithNonExistentId_ReturnsNotFound() { }
[Test] public async Task CancelOrder_WhenAlreadyShipped_ReturnsConflict() { }
```

## Anti-patterns

### Don't Use In-Memory Database for Integration Tests

```csharp
// BAD — hides real SQL behavior, transactions, constraints
services.AddDbContext<AppDbContext>(options =>
    options.UseInMemoryDatabase("TestDb"));

// GOOD — Testcontainers with real database
services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(testContainer.GetConnectionString()));
```

### Don't Test Implementation Details

```csharp
// BAD — testing that a specific repository method was called
mock.Verify(x => x.AddAsync(It.IsAny<Order>()), Times.Once);
mock.Verify(x => x.SaveChangesAsync(), Times.Once);

// GOOD — test the observable outcome
var order = await db.Orders.FindAsync(orderId);
Assert.That(order, Is.Not.Null);
Assert.That(order!.Status, Is.EqualTo(OrderStatus.Created));
```

### Don't Share Mutable State Between Tests

```csharp
// BAD — static shared state
private static readonly AppDbContext SharedDb = CreateDb();

// GOOD — fresh state per test (or use [OneTimeSetUp]/[OneTimeTearDown] for shared fixtures)
private AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()...);
```

### Don't Write Assertion-Free Tests

```csharp
// BAD — no assertion, only checks it doesn't throw
[Test]
public async Task CreateOrder_Works()
{
    await service.CreateAsync(request);
    // "it didn't throw, so it works!" — NO
}

// GOOD — assert the expected outcome
[Test]
public async Task CreateOrder_PersistsOrderToDatabase()
{
    var result = await service.CreateAsync(request);

    var persisted = await db.Orders.FindAsync(result.Value.Id);
    Assert.That(persisted, Is.Not.Null);
    Assert.That(persisted!.CustomerId, Is.EqualTo(request.CustomerId));
}
```

## Decision Guide

| Scenario | Recommendation |
|----------|---------------|
| Testing an API endpoint | `WebApplicationFactory` integration test |
| Testing business logic in isolation | Unit test with fakes/stubs |
| Database-dependent tests | Testcontainers (real DB) |
| Complex response validation | Verify snapshot testing |
| Time-dependent logic | `FakeTimeProvider` |
| External API dependency | `WireMock.Net` or `HttpMessageHandler` stub |
| Parameterized test cases | `[TestCase]` or `[TestCaseSource]` |
| Test data setup | Builder pattern |
| Shared expensive fixture | `[OneTimeSetUp]` with `[OneTimeTearDown]` |
