# ADR-002: Result Pattern Over Exceptions for Control Flow

## Status

Accepted

## Context

.NET applications need a consistent strategy for handling expected failures -- operations that can fail for predictable, domain-relevant reasons (entity not found, validation failed, insufficient permissions, business rule violation). The two dominant approaches are:

### Approach 1: Throw Exceptions

```csharp
public Order GetOrder(Guid id)
{
    var order = _db.Orders.Find(id)
        ?? throw new NotFoundException($"Order {id} not found");
    return order;
}
```

The caller uses `try/catch` or relies on a global exception handler to convert exceptions into HTTP responses.

### Approach 2: Return a Result Type

```csharp
public Result<Order> GetOrder(Guid id)
{
    var order = _db.Orders.Find(id);
    return order is not null
        ? Result.Success(order)
        : Result.Failure<Order>(Error.NotFound("Orders", id));
}
```

The caller inspects the `Result` and maps it to an HTTP response explicitly.

### Evaluation Criteria

1. **Performance.** Throwing an exception captures a full stack trace, which is expensive (measured in microseconds, but significant under load). Result returns are a simple object allocation.
2. **Explicitness.** The method signature should communicate whether an operation can fail. `Result<Order>` makes failure a first-class part of the contract. `Order` with a hidden `throw` does not.
3. **Control flow clarity.** Exceptions create invisible control flow. The reader must know which exceptions a method might throw. Results make all paths visible in the return type.
4. **Composability.** Results chain naturally with pattern matching, LINQ-like operations, and pipeline behaviors. Exception-based flows require nested try/catch or exception filters.
5. **AI-assisted development.** Claude Code generates more correct code when the type system encodes success/failure. With exceptions, Claude must remember (or be told) which exceptions to catch and where.
6. **Integration with ASP.NET Core.** Both approaches work. Exceptions rely on `UseExceptionHandler` middleware. Results map to `TypedResults` in minimal APIs.

### Industry context

The .NET ecosystem has traditionally relied on exceptions for all error handling. However, the trend in modern .NET (and languages like Rust, Go, and Kotlin) is toward explicit error types. Libraries like `FluentResults`, `ErrorOr`, `Ardalis.Result`, and `OneOf` have gained significant adoption.

## Decision

**dotnet-claude-kit uses the Result pattern as the default approach for handling expected failures. Exceptions are reserved for truly exceptional, unexpected situations.**

### What constitutes an "expected failure"

- Entity not found (404)
- Validation failed (400)
- Business rule violated (422)
- Insufficient permissions for an operation (403)
- Conflict / duplicate (409)
- External service returned a known error

### What remains an exception

- Database connection lost unexpectedly
- Null reference due to programming error
- Out of memory
- File system corruption
- Unhandled/unexpected third-party library errors

### Implementation approach

dotnet-claude-kit does not mandate a specific Result library. The recommended pattern is a lightweight, project-owned `Result<T>` type:

```csharp
public sealed record Error(string Code, string Description)
{
    public static Error NotFound(string entity, Guid id) =>
        new($"{entity}.NotFound", $"{entity} with ID {id} was not found.");

    public static Error Validation(string description) =>
        new("Validation", description);

    public static Error Conflict(string description) =>
        new("Conflict", description);
}

public class Result
{
    public bool IsSuccess { get; }
    public Error? Error { get; }

    protected Result(bool isSuccess, Error? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public static Result Success() => new(true, null);
    public static Result Failure(Error error) => new(false, error);
    public static Result<T> Success<T>(T value) => new(value, true, null);
    public static Result<T> Failure<T>(Error error) => new(default, false, error);
}

public class Result<T> : Result
{
    public T? Value { get; }

    internal Result(T? value, bool isSuccess, Error? error)
        : base(isSuccess, error) => Value = value;
}
```

Teams may substitute `FluentResults`, `ErrorOr`, or `Ardalis.Result` if they prefer a library with more features (railway-oriented programming, multiple errors, etc.).

### Mapping Results to HTTP responses

```csharp
public static class ResultExtensions
{
    public static IResult ToProblemDetails(this Result result) =>
        result.IsSuccess
            ? Results.Ok()
            : result.Error!.Code switch
            {
                var code when code.EndsWith(".NotFound") => Results.NotFound(result.Error),
                "Validation" => Results.BadRequest(result.Error),
                "Conflict" => Results.Conflict(result.Error),
                _ => Results.Problem(result.Error.Description)
            };
}
```

## Consequences

### Positive

- **Method signatures are honest.** `Result<Order>` tells the caller that this operation can fail. `Order` with a hidden throw does not.
- **Better performance under load.** Avoiding exception stack trace capture reduces CPU cost for expected failure paths. In benchmarks, Result returns are 100-1000x cheaper than throwing exceptions.
- **Pattern matching friendly.** Results compose naturally with C# pattern matching and switch expressions.
- **Improved AI code generation.** Claude produces more correct code when the type system guides it. The Result type makes success/failure paths explicit in the code Claude reads and generates.
- **Consistent error mapping.** A single `ToProblemDetails()` extension method handles all Result-to-HTTP conversion, replacing scattered `try/catch` blocks.
- **Testable.** Testing a Result return is a simple assertion on `IsSuccess` and `Error`. Testing exception-throwing requires `Assert.Throws` with more ceremony.

### Negative

- **Additional ceremony.** Every handler returns `Result<T>` instead of `T`. This adds a small amount of boilerplate.
- **Unfamiliar to some teams.** Developers accustomed to exception-based patterns may need time to adjust.
- **No built-in Result type in .NET.** The team must either own a simple Result type or take a dependency on a library. (There is ongoing discussion about adding a Result type to the BCL in future .NET versions.)
- **Two error paths.** The application still needs a global exception handler for truly unexpected errors, so there are now two error-handling mechanisms to understand.

### Mitigations

- The `error-handling` skill provides copy-paste Result types, extension methods, and a global exception handler for unexpected errors.
- The `common-antipatterns.md` knowledge document warns against throwing exceptions for expected failures.
- Templates include the Result type and ProblemDetails mapping pre-configured.
