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
    private readonly IFxRateService _fxRateService;

    // Key: InstrumentId
    private readonly ConcurrentDictionary<(string BookName, int InstrumentId), BookElement> _elements = new();
    private readonly ConcurrentDictionary<string, BookInfo> _bookInfos = new();
    private readonly object _lock = new();
    private readonly string _omsIdentifier;

    public event EventHandler<BookElement>? BookElementUpdated;

    public BookManager(ILogger<BookManager> logger,
                       IOrderRouter orderRouter,
                       IInstrumentRepository instrumentRepo,
                       IBookRepository bookRepository,
                       IConfiguration config,
                       IOptions<List<BookConfig>> bookConfigs,
                       IFxRateService fxRateService)
    {
        _logger = logger;
        _orderRouter = orderRouter;
        _instrumentRepo = instrumentRepo;
        _bookRepository = bookRepository;
        _bookConfigs = bookConfigs.Value;
        _omsIdentifier = config["omsIdentifier"] ?? throw new ArgumentNullException("omsIdentifier");
        _fxRateService = fxRateService;
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
                currentElement = BookElement.CreateEmpty(fill.BookName, fill.InstrumentId);
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

        var effectiveFillQty = fill.Side == Side.Buy ? fill.Quantity : Quantity.FromDecimal(fill.Quantity.ToDecimal() * -1);

        // --- 1. Calculate new CUMULATIVE position ---
        var (newSizeAccum, newAvgPriceAccum, pnlDeltaAccum) = CalculateNewPosition(
            currentElement.SizeAccum, currentElement.AvgPriceAccum,
            effectiveFillQty, fill.Price, instrument
        );

        // --- 2. Calculate new SESSION position ---
        var (newSizeSession, newAvgPriceSession, pnlDeltaSession) = CalculateNewPosition(
            currentElement.Size, currentElement.AvgPrice,
            effectiveFillQty, fill.Price, instrument
        );

        // --- 3. Calculate and convert VOLUME ---
        var volume = instrument.ValueInDenominationCurrency(fill.Price, fill.Quantity);
        var volumeInUsdt = _fxRateService.Convert(volume, Currency.USDT) ?? CurrencyAmount.Zero(Currency.USDT);

        // --- 4. Convert PnL to a common currency (USDT) ---
        var pnlAccumInUsdt = _fxRateService.Convert(pnlDeltaAccum, Currency.USDT) ?? CurrencyAmount.Zero(Currency.USDT);
        var pnlSessionInUsdt = _fxRateService.Convert(pnlDeltaSession, Currency.USDT) ?? CurrencyAmount.Zero(Currency.USDT);

        // --- 5. Create the new, updated BookElement ---
        return new BookElement(
            bookName: currentElement.BookName,
            instrumentId: currentElement.InstrumentId,
            lastUpdateTime: fill.Timestamp,

            // Session values
            avgPrice: newAvgPriceSession,
            size: newSizeSession,
            realizedPnL: currentElement.RealizedPnL + pnlSessionInUsdt,
            volume: currentElement.Volume + volumeInUsdt,

            // Cumulative values
            avgPriceAccum: newAvgPriceAccum,
            sizeAccum: newSizeAccum,
            realizedPnLAccum: currentElement.RealizedPnLAccum + pnlAccumInUsdt,
            volumeAccum: currentElement.VolumeAccum + volumeInUsdt
        );
    }

    /// <summary>
    /// A reusable helper that calculates new position size, average price, and realized PnL delta.
    /// This is a pure function.
    /// </summary>
    private (Quantity newSize, Price newAvgPrice, CurrencyAmount realizedPnlDelta) CalculateNewPosition(
        Quantity currentSize, Price currentAvgPrice, Quantity effectiveFillQty, Price fillPrice, Instrument instrument)
    {
        var currentQtyDec = currentSize.ToDecimal();
        var fillQtyDec = effectiveFillQty.ToDecimal();
        var newQtyDec = currentQtyDec + fillQtyDec;

        decimal newAvgPriceDec;
        CurrencyAmount pnlDelta = CurrencyAmount.Zero(instrument.DenominationCurrency);

        const decimal Epsilon = 1e-9m;
        // realized pnl calculation
        if (Math.Sign(currentQtyDec) != Math.Sign(newQtyDec))
        {
            pnlDelta = instrument.ValueInDenominationCurrency(fillPrice, currentSize) - instrument.ValueInDenominationCurrency(currentAvgPrice, currentSize);
        }
        else
        {
            var qtyDiffDecimal = Math.Abs(currentQtyDec) - Math.Abs(newQtyDec);
            var qtyDiff = Quantity.FromDecimal(qtyDiffDecimal);
            if (qtyDiffDecimal > 0)
            {
                pnlDelta = instrument.ValueInDenominationCurrency(fillPrice, qtyDiff) - instrument.ValueInDenominationCurrency(currentAvgPrice, qtyDiff);
                pnlDelta *= Math.Sign(newQtyDec);
            }
        }

        // inverse instrument 
        if (instrument.DenominationCurrency != Currency.USDT)
        {
            var isInverse = true;
            // except XBTUSD
            if (instrument.SourceExchange == ExchangeEnum.BITMEX && instrument.BaseCurrency != Currency.BTC) isInverse = false;
            // except spot
            if (instrument.ProductType == ProductType.Spot) isInverse = false;
            if (isInverse) pnlDelta *= -1m;
        }

        if (Math.Abs(newQtyDec) < Epsilon) // Position is now flat
        {
            newAvgPriceDec = 0;
        }
        else if (Math.Abs(currentQtyDec) < Epsilon || Math.Sign(currentQtyDec) != Math.Sign(newQtyDec)) // Position is new or flipped
        {
            newAvgPriceDec = fillPrice.ToDecimal();
        }
        else if (Math.Abs(newQtyDec) > Math.Abs(currentQtyDec)) // Position increased
        {
            var currentValue = instrument.ValueInDenominationCurrency(currentAvgPrice, currentSize);
            var fillValue = instrument.ValueInDenominationCurrency(fillPrice, effectiveFillQty);
            newAvgPriceDec = instrument.PriceFromValue(currentValue + fillValue, Quantity.FromDecimal(newQtyDec)).ToDecimal();
        }
        else // Position reduced but not flipped
        {
            newAvgPriceDec = currentAvgPrice.ToDecimal();
        }

        return (Quantity.FromDecimal(newQtyDec), Price.FromDecimal(newAvgPriceDec), pnlDelta);
    }

    private async Task RestoreBookStateAsync()
    {
        _logger.LogInformationWithCaller("Restoring book element states from repository...");
        var savedElements = await _bookRepository.LoadAllElementsAsync();

        foreach (var element in savedElements)
        {
            // The element from DB contains the CUMULATIVE state.
            // The SESSION state for the new run starts at zero.
            var key = (element.BookName, element.InstrumentId);

            // Re-create the BookElement, effectively calling CreateWithBasePosition logic.
            _elements[key] = new BookElement(
                element.BookName,
                element.InstrumentId,
                element.LastUpdateTime,
                Price.Zero, Quantity.Zero, CurrencyAmount.Zero(Currency.USDT), CurrencyAmount.Zero(Currency.USDT), // Session starts zero
                element.AvgPriceAccum, element.SizeAccum, element.RealizedPnLAccum, element.VolumeAccum // Cumulative is restored
            );
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
