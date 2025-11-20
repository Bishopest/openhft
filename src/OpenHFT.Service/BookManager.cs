using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Books;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;

namespace OpenHFT.Service;

public class BookManager : IBookManager, IHostedService
{
    private readonly ILogger<BookManager> _logger;
    private readonly IOrderRouter _orderRouter;
    private readonly IInstrumentRepository _instrumentRepo;
    // Key: InstrumentId
    private readonly ConcurrentDictionary<int, BookElement> _elements = new();
    private readonly ConcurrentDictionary<string, Book> _books = new();
    private readonly object _lock = new();

    public event EventHandler<BookElement>? BookElementUpdated;

    public BookManager(ILogger<BookManager> logger, IOrderRouter orderRouter, IInstrumentRepository instrumentRepo)
    {
        _logger = logger;
        _orderRouter = orderRouter;
        _instrumentRepo = instrumentRepo;

        // db 로드 부분
        InitializeBooks();
    }

    private void InitializeBooks()
    {
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _orderRouter.OrderFilled += OnOrderFilled;
        return Task.CompletedTask;
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
        var fillQtyDecimal = fill.Quantity.ToDecimal();
        var fillPriceDecimal = fill.Price.ToDecimal();
        var currentQtyDecimal = currentElement.Quantity.ToDecimal();
        var currentAvgPriceDecimal = currentElement.AvgPrice.ToDecimal();

        decimal newQtyDecimal;
        decimal newAvgPriceDecimal;
        decimal realizedPnlDelta = 0;

        // Determine the direction of the fill relative to the position
        var effectiveFillQtyDecimal = fill.Side == Side.Buy ? fillQtyDecimal : -fillQtyDecimal;

        newQtyDecimal = currentQtyDecimal + effectiveFillQtyDecimal;

        const decimal Epsilon = 0.00000001m;
        bool isCurrentQtyFlat = Math.Abs(currentQtyDecimal) < Epsilon;
        bool isNewQtyFlat = Math.Abs(newQtyDecimal) < Epsilon;

        // realized pnl calculation
        if (Math.Sign(currentQtyDecimal) != Math.Sign(newQtyDecimal))
        {
            realizedPnlDelta = currentQtyDecimal * (fillPriceDecimal - currentAvgPriceDecimal);
        }
        else
        {
            var qtyDiff = Math.Abs(currentQtyDecimal) - Math.Abs(newQtyDecimal);
            if (qtyDiff > 0)
            {
                realizedPnlDelta = Math.Sign(newQtyDecimal) * qtyDiff * (fillPriceDecimal - currentAvgPriceDecimal);
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
            // 2. 포지션이 새로 시작되거나 (isCurrentQtyFlat), 
            //    포지션 방향이 반전되면 (Reversal), 평균 단가를 채결 가격으로 초기화
            //    (참고: 기존 로직에서는 반전 시에도 '증가/감소' 체크가 있었는데, 일반적으로 반전 시 초기화하는 경우가 많아 이를 따름)
            newAvgPriceDecimal = fillPriceDecimal;
        }
        else // 포지션 방향이 유지되며, 포지션이 Flat이 아님
        {
            if (Math.Abs(currentQtyDecimal) < Math.Abs(newQtyDecimal))
            {
                // 3. 포지션이 증가하면 가중 평균 계산 (같은 방향 추가)
                newAvgPriceDecimal = ((currentQtyDecimal * currentAvgPriceDecimal) + (effectiveFillQtyDecimal * fillPriceDecimal)) / newQtyDecimal;
            }
            else
            {
                // 4. 포지션이 감소하면 (Close-out 또는 Partial Close) 기존 평균 유지
                newAvgPriceDecimal = currentAvgPriceDecimal;
            }
        }

        var newBookelement = new BookElement(currentElement.BookName,
                                            currentElement.InstrumentId,
                                            Price.FromDecimal(newAvgPriceDecimal),
                                            Quantity.FromDecimal(newQtyDecimal),
                                            currentElement.RealizedPnL + realizedPnlDelta,
                                            currentElement.VolumeInUsdt + (fillPriceDecimal * fillQtyDecimal),
                                            fill.Timestamp
                                            );
        return newBookelement;
    }

    public BookElement GetBookElement(int instrumentId) => _elements.TryGetValue(instrumentId, out var element) ? element : default;
    public IReadOnlyCollection<BookElement> GetAllBookElements() => _elements.Values.ToList().AsReadOnly();
    public IReadOnlyCollection<BookElement> GetElementsByBookName(string bookName) =>
        _elements.Values.Where(e => e.BookName == bookName).ToList().AsReadOnly();
}
