using System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Books;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Utils;

namespace OpenHFT.Service;

public class BookPersistenceService : IHostedService
{
    private readonly ILogger<BookPersistenceService> _logger;
    private readonly IBookManager _bookManager;
    private readonly IBookRepository _bookRepository;

    public BookPersistenceService(
        ILogger<BookPersistenceService> logger,
        IBookManager bookManager,
        IBookRepository bookRepository)
    {
        _logger = logger;
        _bookManager = bookManager;
        _bookRepository = bookRepository;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("Book Persistence Service is starting...");
        _bookManager.BookElementUpdated += OnBookElementUpdated;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("Book Persistence Service is stopping.");
        _bookManager.BookElementUpdated -= OnBookElementUpdated;
        return Task.CompletedTask;
    }

    private void OnBookElementUpdated(object? sender, BookElement element)
    {
        _logger.LogTrace("Persisting updated BookElement for InstrumentId {Id}", element.InstrumentId);
        _ = _bookRepository.SaveElementAsync(element);
    }
}
