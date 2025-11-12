using System;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.GUI.Data;
using OpenHFT.Oms.Api.WebSocket;
namespace OpenHFT.GUI.Services;

public class SqliteFillRepository : IFillRepository
{
    private readonly string _dataFolderPath;
    private readonly object _lock = new();

    public SqliteFillRepository(IConfiguration configuration)
    {
        _dataFolderPath = configuration["dataFolder"]
            ?? throw new InvalidOperationException("'dataFolder' not configured in config.json.");

        // Ensure the directory exists.
        Directory.CreateDirectory(_dataFolderPath);
    }

    private string GetDbPathForDate(DateTime date)
    {
        return Path.Combine(_dataFolderPath, $"fills_{date:yyyy-MM-dd}.db");
    }

    public void AddFill(Fill fill)
    {
        var dbPath = GetDbPathForDate(DateTime.UtcNow);
        lock (_lock) // Simple lock to prevent concurrent write issues
        {
            using var db = new FillDbContext(dbPath);
            db.Database.EnsureCreated(); // Creates the DB and table if they don't exist
            var fillDbo = FillDbo.FromFill(fill);
            db.Fills.Add(fillDbo);
            db.SaveChanges();
        }
    }

    public IEnumerable<Fill> GetFillsByDate(DateTime date)
    {
        var dbPath = GetDbPathForDate(date);
        if (!File.Exists(dbPath)) return Enumerable.Empty<Fill>();

        using var db = new FillDbContext(dbPath);
        return db.Fills
                     .ToList() // Execute query to get DBOs
                     .Select(dbo => dbo.ToFill()); // Convert to structs in memory
    }

    public IEnumerable<Fill> GetFillsByDateRange(DateTime startDate, DateTime endDate)
    {
        var allFills = new List<Fill>();
        for (var date = startDate.Date; date <= endDate.Date; date = date.AddDays(1))
        {
            allFills.AddRange(GetFillsByDate(date));
        }
        return allFills;
    }

    public IEnumerable<Fill> GetFillsByInstrument(int instrumentId, DateTime date)
    {
        var dbPath = GetDbPathForDate(date);
        if (!File.Exists(dbPath)) return Enumerable.Empty<Fill>();

        using var db = new FillDbContext(dbPath);

        // --- CHANGE: Filter on DBOs, then convert back to Fill structs ---
        return db.Fills
                 .Where(f => f.InstrumentId == instrumentId)
                 .ToList()
                 .Select(dbo => dbo.ToFill());
    }
}