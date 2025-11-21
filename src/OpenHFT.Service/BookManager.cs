using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Books;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;

namespace OpenHFT.Service;

public class BookManager : IBookManager, IHostedService
{
    private readonly ILogger<BookManager> _logger;
    private readonly IOrderRouter _orderRouter;
    private readonly IInstrumentRepository _instrumentRepo;
    private readonly IBookRepository _bookRepository;

    // Key: InstrumentId
    private readonly ConcurrentDictionary<int, BookElement> _elements = new();
    private readonly ConcurrentDictionary<string, Book> _books = new();
    private readonly object _lock = new();

    public event EventHandler<BookElement>? BookElementUpdated;

    public BookManager(ILogger<BookManager> logger, IOrderRouter orderRouter, IInstrumentRepository instrumentRepo, IBookRepository bookRepository)
    {
        _logger = logger;
        _orderRouter = orderRouter;
        _instrumentRepo = instrumentRepo;
        _bookRepository = bookRepository;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("Book Manager is starting...");
        await RestoreBookStateAsync();
        _orderRouter.OrderFilled += OnOrderFilled;
        _logger.LogInformationWithCaller("Book Manager started.");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _orderRouter.OrderFilled -= OnOrderFilled;
        return Task.CompletedTask;
    }

    private void OnOrderFilled(object? sender, Fill fill)
    {
        // 이 체결이 관리 대상 Book에 속하는지 확인
        if (!_elements.TryGetValue(fill.InstrumentId, out var currentElement))
        {
            return;
        }

        lock (_lock)
        {
            var currentBookEle = _elements[fill.InstrumentId];
            var newBookelement = ApplyFill(currentBookEle, fill);
            _elements[fill.InstrumentId] = newBookelement;
            BookElementUpdated?.Invoke(this, newBookelement);
        }
    }

    private BookElement ApplyFill(BookElement currentElement, Fill fill)
    {
        var instrument = _instrumentRepo.GetById(fill.InstrumentId);
        if (instrument == null)
        {
            _logger.LogWarningWithCaller($"Cannot apply fill. Instrument with ID {fill.InstrumentId} not found.");
            return currentElement;
        }

        if (currentElement.InstrumentId != fill.InstrumentId)
        {
            _logger.LogWarningWithCaller($"Cannot apply fill. Instrument IDs do not match.");
            return currentElement;
        }

        var fillQtyDecimal = fill.Quantity.ToDecimal();
        var fillPriceDecimal = fill.Price.ToDecimal();
        // Determine the direction of the fill relative to the position
        var effectiveFillQtyDecimal = fill.Side == Side.Buy ? fillQtyDecimal : -fillQtyDecimal;
        var currentQtyDecimal = currentElement.Size.ToDecimal();
        var currentAvgPriceDecimal = currentElement.AvgPrice.ToDecimal();

        decimal newQtyDecimal;
        decimal newAvgPriceDecimal;
        CurrencyAmount realizedPnlDelta = CurrencyAmount.FromDecimal(0m, instrument.DenominationCurrency);

        newQtyDecimal = currentQtyDecimal + effectiveFillQtyDecimal;

        const decimal Epsilon = 0.00000001m;
        bool isCurrentQtyFlat = Math.Abs(currentQtyDecimal) < Epsilon;
        bool isNewQtyFlat = Math.Abs(newQtyDecimal) < Epsilon;

        // realized pnl calculation
        if (Math.Sign(currentQtyDecimal) != Math.Sign(newQtyDecimal))
        {
            realizedPnlDelta = instrument.ValueInDenominationCurrency(fill.Price, currentElement.Size) - instrument.ValueInDenominationCurrency(currentElement.AvgPrice, currentElement.Size);
        }
        else
        {
            var qtyDiffDecimal = Math.Abs(currentQtyDecimal) - Math.Abs(newQtyDecimal);
            var qtyDiff = Quantity.FromDecimal(qtyDiffDecimal);
            if (qtyDiffDecimal > 0)
            {
                realizedPnlDelta = instrument.ValueInDenominationCurrency(fill.Price, qtyDiff) - instrument.ValueInDenominationCurrency(currentElement.AvgPrice, qtyDiff);
                realizedPnlDelta *= Math.Sign(newQtyDecimal);
            }
        }

        // avg price calculation
        if (isNewQtyFlat)
        {
            // 1. 포지션이 청산되면 평균 단가는 0
            newAvgPriceDecimal = 0m;
        }
        else if (isCurrentQtyFlat || Math.Sign(currentQtyDecimal) != Math.Sign(newQtyDecimal))
        {
            newAvgPriceDecimal = fillPriceDecimal;
        }
        else
        {
            if (Math.Abs(currentQtyDecimal) < Math.Abs(newQtyDecimal))
            {
                // 3. 포지션이 증가하면 가중 평균 계산 (같은 방향 추가)
                var currentValue = instrument.ValueInDenominationCurrency(currentElement.AvgPrice, currentElement.Size);
                var newValue = instrument.ValueInDenominationCurrency(fill.Price, fill.Quantity);
                var multiplier = instrument is CryptoFuture cf ? cf.Multiplier : 1m;
                newAvgPriceDecimal = (currentValue.Amount + newValue.Amount) / newQtyDecimal / multiplier;
            }
            else
            {
                // 4. 포지션이 감소하면 (Close-out 또는 Partial Close) 기존 평균 유지
                newAvgPriceDecimal = currentAvgPriceDecimal;
            }
        }

        var volume = instrument.ValueInDenominationCurrency(fill.Price, fill.Quantity);
        volume = CurrencyAmount.FromDecimal(volume.Amount, Currency.USDT);
        var newBookelement = new BookElement(currentElement.BookName,
                                            currentElement.InstrumentId,
                                            Price.FromDecimal(newAvgPriceDecimal),
                                            Quantity.FromDecimal(newQtyDecimal),
                                            currentElement.RealizedPnL + realizedPnlDelta,
                                            currentElement.VolumeInUsdt + volume,
                                            fill.Timestamp
                                            );
        return newBookelement;
    }

    private async Task RestoreBookStateAsync()
    {
        _logger.LogInformationWithCaller("Restoring book state from repository...");
        var savedElements = await _bookRepository.LoadAllElementsAsync();

        int count = 0;
        foreach (var element in savedElements)
        {
            _elements[element.InstrumentId] = element;

            // Book 객체도 복원/생성
            var book = _books.GetOrAdd(element.BookName, name => new Book(name, new[] { element.InstrumentId }));
            (book.InstrumentIds as HashSet<int>)?.Add(element.InstrumentId); // Add to existing book

            count++;
        }
        _logger.LogInformationWithCaller($"Restored {count} book elements from repository.");
    }

    public BookElement GetBookElement(int instrumentId) => _elements.TryGetValue(instrumentId, out var element) ? element : default;
    public IReadOnlyCollection<BookElement> GetAllBookElements() => _elements.Values.ToList().AsReadOnly();
    public IReadOnlyCollection<BookElement> GetElementsByBookName(string bookName) =>
        _elements.Values.Where(e => e.BookName == bookName).ToList().AsReadOnly();
}
