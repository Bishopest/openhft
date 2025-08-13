using Microsoft.AspNetCore.Mvc;
using OpenHFT.UI.Services;
using OpenHFT.Strategy.Interfaces;

namespace OpenHFT.UI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EngineController : ControllerBase
{
    private readonly HftEngine _engine;
    private readonly IStrategyEngine _strategyEngine;
    private readonly ILogger<EngineController> _logger;

    public EngineController(HftEngine engine, IStrategyEngine strategyEngine, ILogger<EngineController> logger)
    {
        _engine = engine;
        _strategyEngine = strategyEngine;
        _logger = logger;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            IsRunning = _engine.IsRunning,
            IsPaused = _engine.IsPaused,
            EventsProcessed = _engine.EventsProcessed,
            OrdersGenerated = _engine.OrdersGenerated,
            Uptime = _engine.Uptime.ToString(@"hh\:mm\:ss"),
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    [HttpPost("pause")]
    public async Task<IActionResult> Pause()
    {
        await _engine.PauseAsync();
        return Ok(new { Message = "Engine paused" });
    }

    [HttpPost("resume")]
    public async Task<IActionResult> Resume()
    {
        await _engine.ResumeAsync();
        return Ok(new { Message = "Engine resumed" });
    }

    [HttpGet("orderbooks")]
    public IActionResult GetOrderBooks()
    {
        var orderBooks = _engine.GetOrderBooks()
            .Select(book => book.GetSnapshot(10))
            .ToArray();

        return Ok(orderBooks);
    }

    [HttpGet("orderbooks/{symbol}")]
    public IActionResult GetOrderBook(string symbol)
    {
        var orderBook = _engine.GetOrderBook(symbol);
        if (orderBook == null)
        {
            return NotFound($"Order book for symbol '{symbol}' not found");
        }

        return Ok(orderBook.GetSnapshot(20));
    }

    [HttpGet("strategies")]
    public IActionResult GetStrategies()
    {
        var strategies = _strategyEngine.GetStrategies()
            .Select(s => new
            {
                Name = s.Name,
                IsEnabled = s.IsEnabled,
                Symbols = s.Symbols,
                State = s.GetState()
            })
            .ToArray();

        return Ok(strategies);
    }

    [HttpPost("strategies/{name}/enable")]
    public IActionResult EnableStrategy(string name)
    {
        var strategy = _strategyEngine.GetStrategies().FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (strategy == null)
        {
            return NotFound($"Strategy '{name}' not found");
        }

        strategy.IsEnabled = true;
        return Ok(new { Message = $"Strategy '{name}' enabled" });
    }

    [HttpPost("strategies/{name}/disable")]
    public IActionResult DisableStrategy(string name)
    {
        var strategy = _strategyEngine.GetStrategies().FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (strategy == null)
        {
            return NotFound($"Strategy '{name}' not found");
        }

        strategy.IsEnabled = false;
        return Ok(new { Message = $"Strategy '{name}' disabled" });
    }

    [HttpGet("statistics")]
    public IActionResult GetStatistics()
    {
        var engineStats = new
        {
            Engine = new
            {
                IsRunning = _engine.IsRunning,
                IsPaused = _engine.IsPaused,
                EventsProcessed = _engine.EventsProcessed,
                OrdersGenerated = _engine.OrdersGenerated,
                Uptime = _engine.Uptime,
                EventsPerSecond = _engine.EventsProcessed / Math.Max(1, _engine.Uptime.TotalSeconds)
            },
            Strategies = _strategyEngine.GetStatistics(),
            OrderBooks = _engine.GetOrderBooks().Select(book => new
            {
                Symbol = book.Symbol,
                UpdateCount = book.UpdateCount,
                TradeCount = book.TradeCount,
                LastSequence = book.LastSequence,
                BestBid = book.GetBestBid(),
                BestAsk = book.GetBestAsk(),
                Spread = book.GetSpreadTicks(),
                MidPrice = book.GetMidPriceTicks()
            }).ToArray()
        };

        return Ok(engineStats);
    }
}
