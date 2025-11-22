using System;
using System.Collections.Concurrent;
using System.Text.Json;
using OpenHFT.Core.Books;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Utils;
using OpenHFT.Oms.Api.WebSocket;

namespace OpenHFT.GUI.Services;

public class BookCacheService : IBookCacheService, IDisposable
{
    private readonly ILogger<BookCacheService> _logger;
    private readonly IOmsConnectorService _connector;
    private readonly JsonSerializerOptions _jsonOptions;

    // Key: OmsIdentifier, Value: Dictionary of Books for that OMS (Key: BookName)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, BookInfo>> _booksByOms = new();

    // Key: BookName, Value: Dictionary of elements for that Book (Key: OmsIdentifier + InstrumentId)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, BookElement>> _elementsByBookName = new();

    public event Action? OnBookUpdated;

    public BookCacheService(ILogger<BookCacheService> logger, IOmsConnectorService connector, JsonSerializerOptions jsonOptions)
    {
        _logger = logger;
        _connector = connector;
        _jsonOptions = jsonOptions;
        _connector.OnMessageReceived += HandleRawMessage;
        _connector.OnConnectionStatusChanged += HandleConnectionStatusChange;
    }

    private void HandleRawMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("type", out var typeElement) && typeElement.GetString() == "BOOK_UPDATE")
            {
                var bookEvent = JsonSerializer.Deserialize<BookUpdateEvent>(json, _jsonOptions);
                if (bookEvent != null) HandleBookUpdate(bookEvent.Payload);
            }
        }
        catch (Exception ex)
        {
            _logger.LogErrorWithCaller(ex, "Failed to process raw message in BookCacheService.");
        }
    }

    private void HandleBookUpdate(BookUpdatePayload payload)
    {
        var omsId = payload.OmsIdentifier;
        _logger.LogInformationWithCaller($"Processing BOOK_UPDATE from {omsId}. Found {payload.BookInfos.Count()} books and {payload.Elements.Count()} elements.");

        // 1. Update the BookInfo cache
        var omsBooks = _booksByOms.GetOrAdd(omsId, new ConcurrentDictionary<string, BookInfo>());
        foreach (var bookInfo in payload.BookInfos)
        {
            omsBooks[bookInfo.Name] = bookInfo;
        }

        // 2. Update the BookElement cache
        foreach (var element in payload.Elements)
        {
            var bookElements = _elementsByBookName.GetOrAdd(element.BookName, new ConcurrentDictionary<string, BookElement>());
            var uniqueKey = $"{omsId}_{element.InstrumentId}";
            bookElements[uniqueKey] = element;
        }

        // 3. Notify the UI that an update has occurred.
        OnBookUpdated?.Invoke();
    }

    private void HandleConnectionStatusChange((OmsServerConfig Server, ConnectionStatus Status) args)
    {
        if (args.Status == ConnectionStatus.Disconnected || args.Status == ConnectionStatus.Error)
        {
            ClearCacheForOms(args.Server.OmsIdentifier);
        }
    }


    public IEnumerable<string> GetBookNames(string? omsIdentifier = null)
    {
        if (!string.IsNullOrEmpty(omsIdentifier))
        {
            if (_booksByOms.TryGetValue(omsIdentifier, out var books))
            {
                return books.Keys.OrderBy(name => name);
            }

            return Enumerable.Empty<string>();
        }

        return _booksByOms.Values
                          .SelectMany(dict => dict.Keys)
                          .Distinct() // 서로 다른 OMS에 같은 이름의 Book이 있을 수 있으므로 중복 제거
                          .OrderBy(name => name);
    }

    public IEnumerable<BookElement> GetElementsByBookName(string bookName)
    {
        // Get all elements for a given book name, regardless of which OMS they came from.
        return _elementsByBookName.TryGetValue(bookName, out var elements)
            ? elements.Values
            : Enumerable.Empty<BookElement>();
    }

    public void ClearCacheForOms(string omsIdentifier)
    {
        _logger.LogInformationWithCaller($"Clearing book cache for disconnected OMS: {omsIdentifier}");

        // Remove the books associated with this OMS
        _booksByOms.TryRemove(omsIdentifier, out _);

        // Remove elements associated with this OMS from each book
        foreach (var bookName in _elementsByBookName.Keys)
        {
            if (_elementsByBookName.TryGetValue(bookName, out var elements))
            {
                var keysToRemove = elements.Keys.Where(k => k.StartsWith($"{omsIdentifier}_")).ToList();
                foreach (var key in keysToRemove)
                {
                    elements.TryRemove(key, out _);
                }
            }
        }

        OnBookUpdated?.Invoke();
    }

    public void Dispose()
    {
        _connector.OnMessageReceived -= HandleRawMessage;
        _connector.OnConnectionStatusChanged -= HandleConnectionStatusChange;
    }
}
