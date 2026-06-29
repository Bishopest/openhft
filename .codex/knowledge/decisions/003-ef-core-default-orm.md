# ADR-003: EF Core as the Default ORM

## Status

Accepted

## Context

Every data-driven .NET application needs a data access strategy. The .NET ecosystem offers several approaches:

- **Entity Framework Core (EF Core):** Microsoft's first-party ORM. LINQ-to-SQL translation, change tracking, migrations, interceptors, compiled queries, and deep ASP.NET Core integration.
- **Dapper:** Micro-ORM that maps raw SQL results to objects. Minimal abstraction, maximum control. No change tracking, no migrations, no LINQ translation.
- **Raw ADO.NET:** Direct `SqlCommand`/`DbCommand` usage. Lowest level, highest control, most boilerplate.
- **Marten:** Document database abstraction over PostgreSQL's JSONB. Event sourcing support built in.
- **RepoDB:** Hybrid ORM between Dapper and EF Core.

### Evaluation Criteria

1. **Productivity.** How quickly can a developer add CRUD operations, relationships, and queries?
2. **Performance.** What is the query execution overhead? Can hot paths be optimized?
3. **Migrations.** How are schema changes managed across environments?
4. **LINQ support.** Can queries be composed and tested without writing raw SQL?
5. **AI-assisted development.** How well can Claude Code generate correct data access code?
6. **Ecosystem integration.** How well does it integrate with ASP.NET Core, Aspire, and the broader .NET ecosystem?

### Observations

- **EF Core** covers 80-90% of data access needs with excellent productivity. LINQ queries are composable, refactorable, and type-safe. Migrations handle schema evolution. Interceptors enable cross-cutting concerns (audit trails, soft deletes). EF Core 10 adds improved query translation and JSON column support.
- **Dapper** excels for complex reporting queries, bulk operations, and performance-critical paths where EF Core's LINQ translation is suboptimal or generates inefficient SQL.
- **The Repository Pattern over EF Core is an anti-pattern.** `DbContext` is already a Unit of Work. `DbSet<T>` is already a Repository. Wrapping them adds indirection without value in most applications. This is a deliberate, opinionated stance.

## Decision

**EF Core is the default ORM in dotnet-claude-kit. Use `DbContext` directly in feature handlers without a repository abstraction layer.**

### Default usage pattern

```csharp
public sealed class CreateOrderHandler(AppDbContext db)
{
    public async Task<Result<Guid>> HandleAsync(
        CreateOrderRequest request, CancellationToken ct)
    {
        var order = new Order
        {
            CustomerId = request.CustomerId,
            Items = request.Items.Select(i => new OrderItem
            {
                ProductId = i.ProductId,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice
            }).ToList()
        };

        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);

        return Result.Success(order.Id);
    }
}
```

### When to escape to raw SQL / Dapper

EF Core is the default, not an absolute rule. Escape to raw SQL or Dapper when:

| Scenario | Recommended Approach |
|---|---|
| Complex reporting queries with CTEs, window functions, pivots | Dapper or `FromSqlRaw` |
| Bulk insert/update of 10,000+ rows | `ExecuteUpdate`/`ExecuteDelete`, EF Core bulk extensions, or raw SQL |
| Read model for CQRS query side | Dapper for maximum query control |
| Performance-critical hot path (sub-millisecond) | Dapper or compiled EF Core queries |
| Legacy stored procedures that cannot be rewritten | Dapper or `FromSqlRaw` |
| Full-text search with database-specific syntax | Raw SQL via `FromSqlRaw` |

### How to use Dapper alongside EF Core

```csharp
// Register the shared connection
builder.Services.AddScoped<IDbConnection>(sp =>
{
    var db = sp.GetRequiredService<AppDbContext>();
    return db.Database.GetDbConnection();
});

// Use Dapper for complex read queries
public sealed class GetOrderReportHandler(IDbConnection connection)
{
    public async Task<IReadOnlyList<OrderReportRow>> HandleAsync(
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        const string sql = """
            SELECT o.Id, o.CreatedAt, c.Name AS CustomerName,
                   SUM(oi.Quantity * oi.UnitPrice) AS Total
            FROM Orders o
            JOIN Customers c ON c.Id = o.CustomerId
            JOIN OrderItems oi ON oi.OrderId = o.Id
            WHERE o.CreatedAt BETWEEN @From AND @To
            GROUP BY o.Id, o.CreatedAt, c.Name
            ORDER BY Total DESC
            """;

        var results = await connection.QueryAsync<OrderReportRow>(
            sql, new { From = from, To = to });
        return results.AsList();
    }
}
```

### EF Core conventions enforced by dotnet-claude-kit

1. **No repository pattern.** Use `DbContext` directly. `DbSet<T>` IS the repository.
2. **`AsNoTracking()` for read-only queries.** Or better: project to DTOs with `Select()`.
3. **Always pass `CancellationToken`.** Every async EF Core call accepts `ct`.
4. **Configure entities via `IEntityTypeConfiguration<T>`.** Keep `OnModelCreating` clean.
5. **Use `TimeProvider` for audit timestamps.** Not `DateTime.Now` or `DateTime.UtcNow`.
6. **Global query filters for soft deletes and multi-tenancy.** Configure once, apply everywhere.
7. **Interceptors for cross-cutting concerns.** Audit trails, publishing domain events on `SaveChanges`, etc.

## Consequences

### Positive

- **Maximum productivity.** LINQ queries, migrations, and change tracking handle the vast majority of data access needs with minimal code.
- **Type-safe queries.** Compile-time checking of LINQ queries catches errors before runtime. Refactoring entity properties automatically updates queries.
- **Migration management.** EF Core migrations provide a structured, version-controlled approach to schema evolution that raw SQL scripts cannot match for productivity.
- **AI-assisted development.** Claude Code generates highly accurate EF Core LINQ queries because the patterns are well-documented and type-safe. Raw SQL generation is more error-prone.
- **Ecosystem integration.** EF Core integrates with Aspire, health checks, OpenTelemetry, and the ASP.NET Core DI container out of the box.

### Negative

- **Performance ceiling.** EF Core adds overhead compared to raw SQL/Dapper. For the top 1% of performance-critical queries, this matters.
- **LINQ translation gaps.** Some SQL constructs do not have LINQ equivalents or produce suboptimal SQL. The developer must know when to escape to raw SQL.
- **Abstraction leaks.** Change tracking, lazy loading (if enabled), and identity resolution can cause subtle bugs if the developer does not understand EF Core internals.
- **No repository abstraction means direct EF Core coupling.** Feature handlers directly depend on `AppDbContext`. If you ever need to swap ORMs (rare in practice), every handler must change.

### Mitigations

- The `ef-core` skill documents when and how to escape to raw SQL/Dapper.
- The `common-antipatterns.md` knowledge document warns about common EF Core mistakes (tracking in read queries, N+1 queries, missing `AsNoTracking`).
- The `ef-core-specialist` agent detects performance issues and recommends query optimization strategies, including when to drop to Dapper.
