using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.DataBase.Data;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;

namespace OpenHFT.Core.DataBase;

public class SqliteFillRepository : IFillRepository
{
    private readonly ILogger<SqliteFillRepository> _logger;
    private readonly string _filePath;
    private readonly object _lock = new();
    private static readonly SemaphoreSlim _writeSemaphore = new SemaphoreSlim(1, 1);

    public SqliteFillRepository(ILogger<SqliteFillRepository> logger, IConfiguration configuration)
    {
        _logger = logger;
        _filePath = configuration["FILL_DB_PATH"]
            ?? throw new InvalidOperationException("'FILL_DB_PATH' must be configured in .bash_profile.");

        // Ensure the directory exists.
        Directory.CreateDirectory(_filePath);
    }

    private string GetDbPathForDate(DateTime date)
    {
        return Path.Combine(_filePath, $"fills_{date:yyyy-MM-dd}.db");
    }

    public async Task AddFillAsync(Fill fill)
    {
        var dbPath = GetDbPathForDate(DateTime.UtcNow);

        // 동시 쓰기 문제를 방지하기 위해 SemaphoreSlim 사용
        await _writeSemaphore.WaitAsync();
        try
        {
            await using var db = new FillDbContext(dbPath);
            await db.Database.EnsureCreatedAsync(); // 비동기 버전 사용

            var fillDbo = FillDbo.FromFill(fill);
            db.Fills.Add(fillDbo);
            await db.SaveChangesAsync(); // 비동기 버전 사용
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add fill to SQLite database. Fill: {@Fill}", fill);
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task<IEnumerable<Fill>> GetFillsByDateAsync(DateTime date)
    {
        var dbPath = GetDbPathForDate(date);
        if (!File.Exists(dbPath)) return Enumerable.Empty<Fill>();

        await using var db = new FillDbContext(dbPath);
        return (await db.Fills
                     .AsNoTracking()
                     .ToListAsync()) // Execute query to get DBOs
                     .Select(dbo => dbo.ToFill()); // Convert to structs in memory
    }

    public async Task<IEnumerable<Fill>> GetFillsByDateRangeAsync(DateTime startDate, DateTime endDate)
    {
        var allFills = new List<Fill>();
        for (var date = startDate.Date; date <= endDate.Date; date = date.AddDays(1))
        {
            allFills.AddRange(await GetFillsByDateAsync(date));
        }
        return allFills;
    }

    public async Task<IEnumerable<Fill>> GetFillsByInstrumentAsync(int instrumentId, DateTime date)
    {
        var dbPath = GetDbPathForDate(date);
        if (!File.Exists(dbPath)) return Enumerable.Empty<Fill>();

        await using var db = new FillDbContext(dbPath);

        // --- CHANGE: Filter on DBOs, then convert back to Fill structs ---
        return (await db.Fills
                 .AsNoTracking()
                 .Where(f => f.InstrumentId == instrumentId)
                 .ToListAsync())
                 .Select(dbo => dbo.ToFill());
    }

    public string GetDBPath()
    {
        return _filePath;
    }
}