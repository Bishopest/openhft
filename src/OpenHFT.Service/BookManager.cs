using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenHFT.Core.Books;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;

namespace OpenHFT.Service;

public class BookManager : IBookManager, IHostedService
{
    private readonly List<BookConfig> _bookConfigs;
    private readonly ILogger<BookManager> _logger;
    private readonly IOrderRouter _orderRouter;
    private readonly IInstrumentRepository _instrumentRepo;
    private readonly IBookRepository _bookRepository;

    // Key: InstrumentId
    private readonly ConcurrentDictionary<(string BookName, int InstrumentId), BookElement> _elements = new();
    private readonly ConcurrentDictionary<string, BookInfo> _bookInfos = new();
    private readonly object _lock = new();
    private readonly string _omsIdentifier;
    private static readonly HashSet<string> SupportedCurrencies = new() { "BTC", "USDT" };
    private OrderBook? _cachedReferenceOrderBook;

    public event EventHandler<BookElement>? BookElementUpdated;

    public BookManager(ILogger<BookManager> logger,
                       IOrderRouter orderRouter,
                       IInstrumentRepository instrumentRepo,
                       IBookRepository bookRepository,
                       IConfiguration config,
                       IOptions<List<BookConfig>> bookConfigs)
    {
        _logger = logger;
        _orderRouter = orderRouter;
        _instrumentRepo = instrumentRepo;
        _bookRepository = bookRepository;
        _bookConfigs = bookConfigs.Value;
        _omsIdentifier = config["omsIdentifier"] ?? throw new ArgumentNullException("omsIdentifier");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformationWithCaller("Book Manager is starting...");
        InitializeBooksFromConfig(); // 1. 설정 파일로부터 Book 구조를 먼저 생성
        await RestoreBookStateAsync(); // 2. DB로부터 BookElement 상태를 로드
        _orderRouter.OrderFilled += OnOrderFilled;
        _logger.LogInformationWithCaller("Book Manager started.");
    }

    private void InitializeBooksFromConfig()
    {
        _logger.LogInformationWithCaller("Initializing books from configuration...");
        foreach (var config in _bookConfigs)
        {
            var book = new BookInfo(_omsIdentifier, config.BookName, config.Hedgeable);
            _bookInfos[config.BookName] = book;
            _logger.LogInformationWithCaller($"Initialized book '{book.Name}' from {_omsIdentifier}.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _orderRouter.OrderFilled -= OnOrderFilled;
        return Task.CompletedTask;
    }

    private void OnOrderFilled(object? sender, Fill fill)
    {
        var key = (fill.BookName, fill.InstrumentId);

        var instrument = _instrumentRepo.GetById(fill.InstrumentId);
        if (instrument == null)
        {
            _logger.LogWarningWithCaller($"Cannot process fill for unknown instrument ID {fill.InstrumentId}.");
            return;
        }

        lock (_lock)
        {
            // 2. 현재 상태 가져오기 또는 초기화
            if (!_elements.TryGetValue(key, out var currentElement))
            {
                // 초기 BookElement 생성 (Zero State)
                currentElement = new BookElement(
                    fill.BookName,
                    fill.InstrumentId,
                    Price.FromDecimal(0m),
                    Quantity.FromDecimal(0m),
                    CurrencyAmount.FromDecimal(0m, instrument.DenominationCurrency),
                    CurrencyAmount.FromDecimal(0m, Currency.USDT), // Volume is usually in USDT or base currency
                    0 // LastUpdateTime
                );

                _logger.LogInformationWithCaller($"Creating new BookElement for {key} triggered by fill.");
            }

            // 3. 체결 데이터 반영 (ApplyFill은 순수 함수)
            var newBookElement = ApplyFill(currentElement, fill);

            // 4. 저장 및 이벤트 발생
            _elements[key] = newBookElement;
            BookElementUpdated?.Invoke(this, newBookElement);
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
                var newValue = instrument.ValueInDenominationCurrency(fill.Price, Quantity.FromDecimal(effectiveFillQtyDecimal));
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
                                            currentElement.Volume + volume,
                                            fill.Timestamp
                                            );
        return newBookelement;
    }

    private async Task RestoreBookStateAsync()
    {
        _logger.LogInformationWithCaller("Restoring book element states from repository...");
        var savedElements = await _bookRepository.LoadAllElementsAsync();

        foreach (var element in savedElements)
        {
            var key = (element.BookName, element.InstrumentId);
            _elements[key] = element;
        }
    }

    public BookElement GetBookElement(string bookName, int instrumentId)
    {
        return _elements.TryGetValue((bookName, instrumentId), out var element) ? element : default;
    }
    public IReadOnlyCollection<BookElement> GetAllBookElements() => _elements.Values.ToList().AsReadOnly();
    public IReadOnlyCollection<BookInfo> GetAllBookInfos() => _bookInfos.Values.ToList().AsReadOnly();
    public IReadOnlyCollection<BookElement> GetElementsByBookName(string bookName) =>
        _elements.Values.Where(e => e.BookName == bookName).ToList().AsReadOnly();
}
