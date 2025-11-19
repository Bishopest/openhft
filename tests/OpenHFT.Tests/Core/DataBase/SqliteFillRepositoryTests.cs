using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenHFT.Core.DataBase;
using OpenHFT.Core.DataBase.Data;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.GUI.Services;

namespace OpenHFT.Tests.Core.DataBase
{
    [TestFixture]
    public class SqliteFillRepositoryTests
    {
        private string _testDirectory = string.Empty;
        private IConfiguration _configuration = null!;
        private IFillRepository _repository = null!;

        [SetUp]
        public void SetUp()
        {
            // Create a unique temporary directory for each test run to ensure isolation.
            _testDirectory = Path.Combine(Path.GetTempPath(), "FillRepoTests", Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDirectory);

            // Create a mock IConfiguration that points to our temporary directory.
            var inMemorySettings = new Dictionary<string, string>
            {
                { "FILL_DB_PATH", _testDirectory }
            };
            _configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings!)
                .Build();

            // Instantiate the repository to be tested.
            _repository = new SqliteFillRepository(new NullLogger<SqliteFillRepository>(), _configuration);
        }

        [TearDown]
        public void TearDown()
        {
            // Clean up the temporary directory and all database files.
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, true);
            }
        }

        [Test]
        public async Task AddFill_ShouldStoreFillInDatabase()
        {
            // Arrange
            var fill = new Fill(1001, 12345, "EXCH001", "EXEC001", Side.Buy, Price.FromDecimal(50000m), Quantity.FromDecimal(1.5m), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            // Act
            await _repository.AddFillAsync(fill);

            // Assert
            // We'll directly access the database file to verify the data was written.
            var dbPath = Path.Combine(_testDirectory, $"fills_{DateTime.UtcNow:yyyy-MM-dd}.db");
            Assert.That(File.Exists(dbPath), Is.True);

            using var db = new FillDbContext(dbPath);
            var storedFillDbo = db.Fills.FirstOrDefault();

            storedFillDbo.Should().NotBeNull();
            storedFillDbo!.InstrumentId.Should().Be(fill.InstrumentId);
            storedFillDbo.FilledPrice.Should().Be(fill.Price.ToDecimal());
            storedFillDbo.FilledQuantity.Should().Be(fill.Quantity.ToDecimal());
        }

        [Test]
        public async Task GetFillsByDate_ShouldReturnCorrectFills()
        {
            // Arrange
            var today = DateTime.UtcNow;
            var yesterday = today.AddDays(-1);

            var fillToday1 = new Fill(1001, 1, "E1", "X1", Side.Buy, Price.FromDecimal(100), Quantity.FromDecimal(1), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var fillToday2 = new Fill(1002, 2, "E2", "X2", Side.Sell, Price.FromDecimal(200), Quantity.FromDecimal(2), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var fillYesterday = new Fill(1001, 3, "E3", "X3", Side.Buy, Price.FromDecimal(300), Quantity.FromDecimal(3), DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds());

            await _repository.AddFillAsync(fillToday1);
            await _repository.AddFillAsync(fillToday2);

            // Manually create a repository for yesterday to simulate data from another day
            var yesterdayRepo = new SqliteFillRepository(new NullLogger<SqliteFillRepository>(), _configuration);
            var yesterdayFillDbo = FillDbo.FromFill(fillYesterday);
            var yesterdayDbPath = Path.Combine(_testDirectory, $"fills_{yesterday:yyyy-MM-dd}.db");
            using (var db = new FillDbContext(yesterdayDbPath)) { db.Database.EnsureCreated(); db.Fills.Add(yesterdayFillDbo); db.SaveChanges(); }

            // Act
            var result = await _repository.GetFillsByDateAsync(today);

            // Assert
            result.Should().HaveCount(2);
            result.Should().Contain(f => f.ClientOrderId == 1);
            result.Should().Contain(f => f.ClientOrderId == 2);
            result.Should().NotContain(f => f.ClientOrderId == 3);
        }

        [Test]
        public async Task GetFillsByInstrument_ShouldReturnFilteredFills()
        {
            // Arrange
            var today = DateTime.UtcNow;
            var fill1_Inst1001 = new Fill(1001, 1, "E1", "X1", Side.Buy, Price.FromDecimal(100), Quantity.FromDecimal(1), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var fill2_Inst1002 = new Fill(1002, 2, "E2", "X2", Side.Sell, Price.FromDecimal(200), Quantity.FromDecimal(2), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var fill3_Inst1001 = new Fill(1001, 3, "E3", "X3", Side.Buy, Price.FromDecimal(300), Quantity.FromDecimal(3), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            await _repository.AddFillAsync(fill1_Inst1001);
            await _repository.AddFillAsync(fill2_Inst1002);
            await _repository.AddFillAsync(fill3_Inst1001);

            // Act
            var result = await _repository.GetFillsByInstrumentAsync(1001, today);

            // Assert
            result.Should().HaveCount(2);
            result.Should().OnlyContain(f => f.InstrumentId == 1001);
            result.Should().NotContain(f => f.InstrumentId == 1002);
        }
    }
}