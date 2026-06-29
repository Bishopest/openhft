# Breaking Changes: .NET 9 to .NET 10 Migration Guide

> Last updated: February 2026 -- .NET 10 GA (November 2025)
>
> Sources: [Breaking changes in .NET 10](https://learn.microsoft.com/en-us/dotnet/core/compatibility/10), [ASP.NET Core 9 to 10 migration](https://learn.microsoft.com/en-us/aspnet/core/migration/90-to-100), [EF Core 10 breaking changes](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/breaking-changes)

---

## TFM and SDK Changes

### Update Target Framework Moniker

```diff
<PropertyGroup>
-  <TargetFramework>net9.0</TargetFramework>
+  <TargetFramework>net10.0</TargetFramework>
</PropertyGroup>
```

### Update global.json

```diff
{
  "sdk": {
-    "version": "9.0.304"
+    "version": "10.0.100"
  }
}
```

### Update Package References

All `Microsoft.AspNetCore.*`, `Microsoft.EntityFrameworkCore.*`, `Microsoft.Extensions.*`, and `System.Net.Http.Json` packages must be updated to 10.0.0 or later.

### C# 14 Language Version

.NET 10 ships with C# 14. Set `<LangVersion>14</LangVersion>` or `<LangVersion>latest</LangVersion>`. New language features include extension members, the `field` keyword, and broader `params` collection support.

---

## ASP.NET Core Breaking Changes

### WithOpenApi Deprecated

The `WithOpenApi()` extension methods are deprecated (diagnostic `ASPDEPR002`). Use the built-in `AddOpenApi()` / `MapOpenApi()` instead, which generates OpenAPI documents natively without requiring the `Microsoft.AspNetCore.OpenApi` package's `WithOpenApi` extension.

### Microsoft.OpenApi Upgraded to 2.0

The OpenAPI library used internally has been upgraded to `Microsoft.OpenApi` 2.0 GA. If you directly reference `Microsoft.OpenApi` types (e.g., in OpenAPI document transformers), review API changes in the 2.0 release.

### Cookie Authentication Redirect Behavior Changed

Cookie-based authentication no longer issues redirects for unauthenticated/unauthorized requests to API endpoints. Previously, the handler had special logic to detect API endpoints and issue redirects. This has been removed -- API endpoints now return 401/403 directly.

### Blazor Changes

- **NavLinkMatch.All** now ignores query strings when matching URLs.
- **HttpClient streaming** is enabled by default in Blazor WebAssembly.
- **Fragment deprecated** in favor of `NotFoundPage`.
- **blazor.boot.json** has been inlined into the `dotnet.js` script.
- **BlazorCacheBootResources** MSBuild property removed -- all client-side files are now fingerprinted and cached by the browser.
- **Standalone Blazor WASM environment** is now set via `<WasmApplicationEnvironmentName>` MSBuild property instead of `launchSettings.json`.

### Non-Incremental Source Generators Obsoleted

Source generators that are not incremental are now obsoleted. If you maintain custom source generators, they must be rewritten as incremental source generators.

---

## Entity Framework Core 10 Breaking Changes

### EF Tools Require `--framework` for Multi-Targeted Projects (Medium Impact)

When running EF tools (`dotnet ef`) on a project with `<TargetFrameworks>` (plural), you must specify `--framework`:

```bash
dotnet ef migrations add MyMigration --framework net10.0
dotnet ef database update --framework net10.0
```

Without this, EF tools throw: "The project targets multiple frameworks. Use the --framework option to specify which target framework to use."

### Application Name Injected into Connection Strings (Low Impact)

EF now injects an `Application Name` into connection strings that do not already have one. This can cause separate connection pools when mixing EF and non-EF data access (Dapper, raw ADO.NET), potentially triggering distributed transaction escalation inside `TransactionScope`.

**Mitigation:** Explicitly set `Application Name` in your connection string.

### SQL Server JSON Data Type Used by Default on Azure SQL (Low Impact)

If you use `UseAzureSql()` or configure compatibility level 170+, EF maps JSON columns to the native `json` type instead of `nvarchar(max)`. Upgrading generates a migration that alters existing columns.

**Mitigation:** Set compatibility level below 170, or explicitly configure `HasColumnType("nvarchar(max)")` on affected properties.

### Parameterized Collections Use Multiple Parameters (Low Impact)

`Contains()` queries now translate to `WHERE [Id] IN (@p1, @p2, @p3)` instead of `OPENJSON(@json)`. This provides better cardinality estimates but may affect query plan caching for large collections.

**Mitigation:** Configure `UseParameterizedCollectionMode(ParameterTranslationMode.Parameter)` to revert to JSON-based translation, or use `EF.Parameter(ids)` per query.

### ExecuteUpdate Accepts Non-Expression Lambda (Low Impact)

`ExecuteUpdate`/`ExecuteUpdateAsync` now accepts a `Func<...>` instead of `Expression<Func<...>>`. Code that built expression trees dynamically for the setters argument will no longer compile but can be replaced with simpler imperative code using `if` statements inside the lambda.

### Complex Type Column Names Uniquified (Low Impact)

Complex type column names are now uniquified by appending a number when duplicates exist. Nested complex type properties use the full path in column names (e.g., `Complex_NestedComplex_Property` instead of `NestedComplex_Property`).

**Mitigation:** Explicitly configure column names with `HasColumnName()` if you need to preserve existing names.

### Microsoft.Data.Sqlite DateTime/TimeZone Changes (High Impact)

Three related changes affect SQLite DateTime handling:
- `GetDateTimeOffset` on timestamps without an offset now assumes UTC (was local time).
- Writing `DateTimeOffset` into REAL columns now converts to UTC first.
- `GetDateTime` on timestamps with an offset now returns UTC with `DateTimeKind.Utc`.

**Mitigation:** Set `AppContext.SetSwitch("Microsoft.Data.Sqlite.Pre10TimeZoneHandling", true)` to revert temporarily.

---

## Core .NET Libraries (BCL) Breaking Changes

### C# 14 Overload Resolution with Span Parameters

C# 14 may select different overloads when `Span<T>` / `ReadOnlySpan<T>` overloads are available. This is a behavioral change that can affect method dispatch without compile errors.

### BufferedStream.WriteByte No Longer Performs Implicit Flush

`BufferedStream.WriteByte` no longer flushes the buffer implicitly. Code relying on single-byte writes triggering flushes must call `Flush()` explicitly.

### System.Linq.AsyncEnumerable Included in Core Libraries

`System.Linq.AsyncEnumerable` is now part of the core libraries. If you have a NuGet reference to a third-party `System.Linq.Async` package, you may encounter ambiguous reference errors.

**Mitigation:** Remove the third-party `System.Linq.Async` package reference if present.

### Default Trace Context Propagator Updated to W3C Standard

The default trace context propagator now uses the W3C standard. If you depend on the previous propagation format, configure your `ActivitySource` explicitly.

### FilePatternMatch.Stem Changed to Non-Nullable

`FilePatternMatch.Stem` is now non-nullable. Code that null-checks this property may produce compiler warnings.

### .NET Runtime No Longer Provides Default Termination Signal Handlers

The runtime no longer installs default handlers for `SIGTERM`. If your application relies on graceful shutdown via `SIGTERM`, ensure you register handlers explicitly (e.g., via `IHostApplicationLifetime`).

---

## Extensions Breaking Changes

### BackgroundService Runs All of ExecuteAsync as a Task

`BackgroundService` now runs the entirety of `ExecuteAsync` as a `Task`. Previously, only the part after the first `await` ran asynchronously. If your `ExecuteAsync` does synchronous work before the first `await`, it now runs on a thread pool thread instead of blocking startup.

### Null Values Preserved in Configuration

Configuration now preserves `null` values instead of removing keys with null values. Code that checks for key existence to detect "unset" values may behave differently.

### Console Log Message No Longer Duplicated

The console logger no longer duplicates the message in JSON output mode. If you parse console log output, adjust your parsing logic.

### ProviderAliasAttribute Moved Assembly

`ProviderAliasAttribute` moved to `Microsoft.Extensions.Logging.Abstractions`. If you reference it by assembly-qualified name, update accordingly.

---

## SDK and MSBuild Breaking Changes

### SLNX Is the Default Solution Format

`dotnet new sln` now creates `.slnx` files instead of `.sln`. Existing `.sln` files continue to work. If your tooling does not support `.slnx`, use `dotnet new sln --format sln` explicitly.

### dotnet restore Audits Transitive Packages

NuGet audit now checks transitive packages by default. This may surface new security warnings for transitive dependencies.

### PackageReference Without Version Raises an Error

A `<PackageReference>` without a `Version` attribute now raises error `NU1015` unless you are using Central Package Management (`Directory.Packages.props`).

### Single-File Apps No Longer Look for Native Libraries in Executable Directory

Single-file published apps no longer search the executable directory for native libraries by default. Use `DllImportSearchPath.AssemblyDirectory` explicitly if needed.

### Default Container Images Use Ubuntu

The default .NET container base images now use Ubuntu instead of Debian. This may affect applications that depend on Debian-specific packages or paths.

---

## Serialization Breaking Changes

### System.Text.Json Checks for Property Name Conflicts

`System.Text.Json` now validates that serialized property names are unique within a type. If you have properties that serialize to the same JSON name (e.g., via `[JsonPropertyName]`), this now throws at serialization time.

### XmlSerializer No Longer Ignores Obsolete Properties

`XmlSerializer` now serializes properties marked with `[Obsolete]`. Previously, these were silently skipped.

---

## Cryptography Breaking Changes

### OpenSSL 1.1.1 or Later Required on Unix

.NET 10 requires OpenSSL 1.1.1+ on Unix/Linux. Older OpenSSL versions are no longer supported.

### OpenSSL Cryptographic Primitives Not Supported on macOS

OpenSSL-based cryptographic primitives are no longer supported on macOS. Use the platform's native cryptography APIs.

---

## Migration Checklist

1. Update `TargetFramework` to `net10.0`
2. Update `global.json` SDK version to `10.0.100`
3. Update all `Microsoft.*` package references to 10.0.x
4. Search for `WithOpenApi()` calls and migrate to built-in OpenAPI
5. Add `--framework` to any EF tooling scripts for multi-targeted projects
6. Set explicit `Application Name` in connection strings if mixing EF + raw ADO.NET
7. Check for `System.Linq.Async` NuGet references (now built-in)
8. Review `System.Text.Json` serialized types for property name conflicts
9. Verify SQLite `DateTime`/`DateTimeOffset` handling if using Microsoft.Data.Sqlite
10. Test container builds (base image changed from Debian to Ubuntu)
11. Update CI scripts if they reference `.sln` files explicitly (new default is `.slnx`)
