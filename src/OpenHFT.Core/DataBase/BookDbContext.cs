using System;
using Microsoft.EntityFrameworkCore;

namespace OpenHFT.Core.DataBase;

public class BookDbContext : DbContext
{
    public DbSet<BookElementDbo> BookElements { get; set; }
    private readonly string _dbPath;

    public BookDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite($"Data Source={_dbPath}");

}
