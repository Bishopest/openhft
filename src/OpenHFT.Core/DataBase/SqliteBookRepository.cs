using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Books;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Utils;

namespace OpenHFT.Core.DataBase;

public class SqliteBookRepository : IBookRepository
{
    private readonly ILogger<SqliteBookRepository> _logger;
    private readonly string _databaseFile;
    private static readonly SemaphoreSlim _dbLock = new SemaphoreSlim(1, 1);

    public SqliteBookRepository(IConfiguration configuration, ILogger<SqliteBookRepository> logger)
    {
        _logger = logger;
        // 환경 변수 또는 설정 파일에서 경로를 가져옵니다.
        var dataFolder = configuration["BOOK_DB_PATH"]
            ?? throw new InvalidOperationException("'BOOK_DB_PATH' must be configured in .bash_profile.");

        _databaseFile = Path.Combine(dataFolder, "book_state.db");

        // DB 초기화
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_databaseFile)!);
            using var db = new BookDbContext(_databaseFile);
            db.Database.EnsureCreated();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize the SQLite book state database.");
            throw;
        }
    }

    public async Task SaveElementAsync(BookElement element)
    {
        await _dbLock.WaitAsync();
        try
        {
            await using var db = new BookDbContext(_databaseFile);

            var existing = await db.BookElements.FindAsync(element.BookName, element.InstrumentId);
            var dbo = BookElementDbo.FromBookElement(element);

            if (existing != null)
            {
                // Update
                db.Entry(existing).CurrentValues.SetValues(dbo);
            }
            else
            {
                // Insert
                db.BookElements.Add(dbo);
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save BookElement for InstrumentId {InstrumentId}", element.InstrumentId);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<IEnumerable<BookElement>> LoadAllElementsAsync()
    {
        if (!File.Exists(_databaseFile)) return Enumerable.Empty<BookElement>();

        await _dbLock.WaitAsync();
        try
        {
            await using var db = new BookDbContext(_databaseFile);

            // 1. DB에서 DBO 리스트 가져오기
            var dbos = await db.BookElements.AsNoTracking().ToListAsync();

            // 2. 메모리 내에서 변환 수행 (여기서 에러가 나는지 확인)
            var result = new List<BookElement>();
            foreach (var dbo in dbos)
            {
                try
                {
                    result.Add(dbo.ToBookElement());
                }
                catch (Exception ex)
                {
                    // ★ 여기서 어떤 데이터 때문에 죽는지 로그로 확인 ★
                    _logger.LogErrorWithCaller(ex, $"Failed to convert DBO to BookElement. InstrumentId: {dbo.InstrumentId}, Currency: {dbo.RealizedPnLCurrency}");
                    // 필요하다면 여기서 throw를 다시 하거나, 잘못된 데이터는 스킵하고 진행
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "Error loading book elements from DB.");
            return Enumerable.Empty<BookElement>();
        }
        finally
        {
            _dbLock.Release();
        }
    }
}
