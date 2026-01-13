using System;
using Microsoft.Extensions.Logging;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;

namespace OpenHFT.Core.Orders;

public abstract class AlgoOrder : Order, IAlgoOrder, IDisposable
{
    private volatile bool _isAlgoActive = false;
    private readonly ILogger _logger;
    private readonly IMarketDataManager _marketDataManager;
    private readonly string _subscriberId;
    protected readonly object StrategyLock = new();
    public bool IsAlgoRunning => _isAlgoActive;

    public AlgoOrder(long clientOrderId, int instrumentId, Side side, string bookName, IOrderRouter router, IOrderGateway gateway, ILogger<Order> logger, IMarketDataManager marketDataManager)
        : base(clientOrderId, instrumentId, side, bookName, router, gateway, logger)
    {
        _logger = logger;
        _marketDataManager = marketDataManager;
        _subscriberId = $"AlgoOrder_{ClientOrderId}";
        this.StatusChanged += OnSelfStatusChanged;
    }

    public override async Task SubmitAsync(CancellationToken cancellationToken = default)
    {
        // 1. 오더북 확인
        var book = _marketDataManager.GetOrderBook(InstrumentId);
        if (book == null)
        {
            throw new InvalidOperationException($"AlgoOrder {ClientOrderId}: OrderBook is not ready. Cannot calculate initial price.");
        }

        // 2. 전략에 따른 목표 가격 계산
        var targetPrice = CalculateTargetPrice(book);

        // 3. 가격 유효성 검사 (실패 시 예외 발생 -> Hedger가 잡아서 롤백)
        if (targetPrice.ToTicks() <= 0)
        {
            throw new InvalidOperationException($"AlgoOrder {ClientOrderId}: Calculated initial price is invalid ({targetPrice}). Market might be empty.");
        }

        // 4. 가격 설정
        this.Price = targetPrice;
        _logger.LogInformationWithCaller($"AlgoOrder {ClientOrderId}: Initial price set to {this.Price}.");

        // 5. 실제 전송
        await base.SubmitAsync(cancellationToken);
    }

    public void OnMarketDataUpdated(OrderBook book)
    {
        if (!_isAlgoActive) return;

        lock (StrategyLock)
        {
            if (!_isAlgoActive) return;

            if (Status == OrderStatus.NewRequest ||
                Status == OrderStatus.ReplaceRequest ||
                Status == OrderStatus.CancelRequest)
            {
                return;
            }

            EvaluateAlgoLogic(book);
        }
    }

    private void OnSelfStatusChanged(object? sender, OrderStatusReport report)
    {
        if (report.Status == OrderStatus.Filled ||
            report.Status == OrderStatus.Cancelled ||
            report.Status == OrderStatus.Rejected)
        {
            StopAlgo();
            return;
        }

        if (!_isAlgoActive && !string.IsNullOrEmpty(this.ExchangeOrderId))
        {
            if (report.Status == OrderStatus.New || report.Status == OrderStatus.PartiallyFilled)
            {
                StartMarketSubscription();
                _isAlgoActive = true;
            }
        }
    }

    protected void EvaluateAlgoLogic(OrderBook book)
    {
        var targetPrice = CalculateTargetPrice(book);

        if (targetPrice.ToTicks() > 0 && targetPrice != this.Price)
        {
            if (SupportsOrderReplacement)
            {
                _ = ReplaceAsync(targetPrice, OrderType.Limit);
            }
            else
            {
                _ = CancelAsync();
            }
        }
    }

    protected abstract Price CalculateTargetPrice(OrderBook book);

    private void StartMarketSubscription()
    {
        _marketDataManager.SubscribeOrderBook(InstrumentId, _subscriberId, OnOrderBookUpdated);
    }

    private void OnOrderBookUpdated(object? sender, OrderBook book)
    {
        OnMarketDataUpdated(book);
    }

    private void StopMarketSubscription()
    {
        _marketDataManager.UnsubscribeOrderBook(InstrumentId, _subscriberId);
    }

    public void StopAlgo()
    {
        if (_isAlgoActive)
        {
            _isAlgoActive = false;
            StopMarketSubscription(); // [중요] 구독 해지
        }
    }

    public void Dispose()
    {
        StopAlgo();
    }
}
