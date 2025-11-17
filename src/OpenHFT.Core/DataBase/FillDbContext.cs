using Microsoft.EntityFrameworkCore;

namespace OpenHFT.Core.DataBase.Data;

public class FillDbContext : DbContext
{
    public DbSet<FillDbo> Fills { get; set; }

    private readonly string _dbPath;

    // The DbContext will now receive the path from the repository.
    public FillDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite($"Data Source={_dbPath}");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FillDbo>().HasKey(f => f.FillDboId);
    }
}
