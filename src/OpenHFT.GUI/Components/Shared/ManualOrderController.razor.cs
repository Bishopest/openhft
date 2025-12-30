using System;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using OpenHFT.Core.Books;
using OpenHFT.Core.Configuration;
using OpenHFT.Core.Instruments;
using OpenHFT.Core.Interfaces;
using OpenHFT.Core.Models;
using OpenHFT.Core.Utils;
using OpenHFT.GUI.Services;
using OpenHFT.Oms.Api.WebSocket;
using Serilog;

namespace OpenHFT.GUI.Components.Shared;

// A simple model to bind the form data
public class ManualOrderModel
{
    public string? OrderId { get; set; }
    public Price OrderPrice { get; set; } = Price.FromDecimal(0m);
    public Quantity Size { get; set; } = Quantity.FromDecimal(0m);
    public bool PostOnly { get; set; } = true; // Default to true
}

public class ManualOrderDisplayItem
{
    public string OmsIdentifier { get; set; } = string.Empty;
    public OrderStatusReport Report { get; set; }
}

public partial class ManualOrderController : ComponentBase, IDisposable
{
    // --- Injected Services ---
    [Inject] private IOmsConnectorService OmsConnector { get; set; } = default!;
    [Inject] private IInstrumentRepository InstrumentRepository { get; set; } = default!;
    [Inject] private IBookCacheService BookCache { get; set; } = default!;
    [Inject] private IOrderCacheService OrderCache { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IConfiguration Configuration { get; set; } = default!;
    [Inject] private ILogger<ManualOrderController> Logger { get; set; } = default!;

    // --- Component State ---
    private MudForm _form = null!;
    private ManualOrderModel _model = new();

    // --- Form Select Options ---
    private List<OmsServerConfig> _availableOmsServers = new();
    private List<string> _availableBookNames = new();
    private List<Instrument> _availableInstruments = new();

    // --- Form Bound Values ---
    [Parameter] public string? SelectedOmsIdentifier { get; set; }
    [Parameter] public Instrument? SelectedInstrument { get; set; }
    [Parameter] public string? SelectedBookName { get; set; }

    private bool _isDisposed = false;

    /// <summary>
    /// This property is bound to the Price MudNumericField.
    /// It gets/sets the value from/to the _model, converting between decimal and Price.
    /// </summary>
    private decimal? OrderPriceDecimal
    {
        get => _model.OrderPrice.ToDecimal();
        set => _model.OrderPrice = Price.FromDecimal(value ?? 0);
    }

    /// <summary>
    /// This property is bound to the Size MudNumericField.
    /// It gets/sets the value from/to the _model, converting between decimal and Quantity.
    /// </summary>
    private decimal? OrderSizeDecimal
    {
        get => _model.Size.ToDecimal();
        set => _model.Size = Quantity.FromDecimal(value ?? 0);
    }

    // --- Active Manual Orders Table ---
    // 구조: OmsIdentifier (string) -> ExchangeOrderId (string) -> Order객체
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ManualOrderDisplayItem>> _activeManualOrdersByOms = new();

    protected override void OnInitialized()
    {
        // Load initial data for selectors
        _availableOmsServers = Configuration.GetSection("oms").Get<List<OmsServerConfig>>() ?? new List<OmsServerConfig>();
        _availableInstruments = InstrumentRepository.GetAll().ToList();
        // Subscribe to events
        // TO-DO=> make OrderCache handle orders in instances vs manual
        OrderCache.OnManualOrderUpdated += HandleManualOrderUpdate;
    }

    protected override void OnParametersSet()
    {
        // When OMS selection changes, update the list of available book names
        if (!string.IsNullOrEmpty(SelectedOmsIdentifier))
        {
            _availableBookNames = BookCache.GetBookNames(SelectedOmsIdentifier).ToList();
        }
        else
        {
            _availableBookNames.Clear();
        }
    }

    /// <summary>
    /// Public method to be called from BookPage when a row is clicked.
    /// </summary>
    public void SetOrderDetailsFromBookElement(BookElement element)
    {
        var omsId = BookCache.GetOmsIdentifierByBookName(element.BookName);
        if (omsId is null)
        {

            Logger.LogWarningWithCaller($"can not find oms identifier from book {element.BookName}");
            return;
        }

        SelectedOmsIdentifier = omsId;
        SelectedBookName = element.BookName;
        SelectedInstrument = InstrumentRepository.GetById(element.InstrumentId);

        // Reset the model when details are pre-filled
        _model = new ManualOrderModel();
        StateHasChanged();
    }

    /// <summary>
    /// This function is called by the MudAutocomplete component whenever the user types.
    /// It filters the list of available instruments based on the search text.
    /// </summary>
    private async Task<IEnumerable<Instrument>> SearchInstruments(string searchText, CancellationToken cancellationToken)
    {
        // Simulate a small delay to avoid excessive searching on fast typing
        await Task.Delay(5);

        if (string.IsNullOrWhiteSpace(searchText))
        {
            return _availableInstruments.Take(10);
        }

        return _availableInstruments.Where(i =>
            i.Symbol.Contains(searchText, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// This function tells the MudAutocomplete how to display a selected Instrument.
    /// </summary>
    private string ConvertInstrumentToString(Instrument? instrument)
    {
        return instrument is not null ? $"{instrument.Symbol} ({instrument.SourceExchange}) ({instrument.ProductType})" : string.Empty;
    }

    private void TogglePostOnly(bool value) => _model.PostOnly = value;

    private async Task HandleSubmit(bool isBuy)
    {
        await _form.Validate();
        if (!_form.IsValid) return;

        var price = Price.FromDecimal(OrderPriceDecimal ?? 0);
        var size = Quantity.FromDecimal(OrderSizeDecimal ?? 0);
        var sideString = isBuy ? "BUY" : "SELL";

        var parameters = new DialogParameters<OrderConfirmationDialog>
        {
            ["Title"] = $"Confirm Manual {sideString} Order",
            ["Color"] = isBuy ? Color.Success : Color.Error,
            ["SubmitText"] = sideString,
            ["Oms"] = SelectedOmsIdentifier,
            ["Instrument"] = SelectedInstrument?.Symbol,
            ["Book"] = SelectedBookName,
            ["Price"] = price,
            ["Size"] = size,
            ["PostOnly"] = _model.PostOnly
        };
        var dialog = await DialogService.ShowAsync<OrderConfirmationDialog>($"Confirm {sideString}", parameters);
        if (dialog is null) return;

        var result = await dialog.Result;
        if (!result.Canceled)
        {
            await SendOrderCommand(price, size, isBuy);
        }
    }

    private async Task SendOrderCommand(Price price, Quantity size, bool isBuy)
    {
        var targetServer = _availableOmsServers.FirstOrDefault(s => s.OmsIdentifier == SelectedOmsIdentifier);
        if (targetServer is null || SelectedInstrument is null || SelectedBookName is null)
        {
            Snackbar.Add("Invalid order parameters.", Severity.Error);
            return;
        }

        var payload = new ManualOrderPayload(
            InstrumentId: SelectedInstrument.InstrumentId,
            BookName: SelectedBookName,
            Price: price,
            Size: size,
            IsBuy: isBuy, // <-- Pass the side flag
            PostOnly: _model.PostOnly
        );
        var command = new ManualOrderCommand(payload);

        await OmsConnector.SendCommandAsync(targetServer, command);
        Snackbar.Add($"Manual {(isBuy ? "BUY" : "SELL")} order command sent.", Severity.Info);
        _form.ResetValidation();
    }

    private async Task HandleCancelOrderInTable(ManualOrderDisplayItem item)
    {
        if (item.Report.ExchangeOrderId is null)
        {
            return;
        }
        var orderIdToCancel = item.Report.ExchangeOrderId;

        var parameters = new DialogParameters<OrderConfirmationDialog>
        {
            ["Title"] = "Confirm Order Cancellation",
            ["Content"] = new RenderFragment(builder => builder.AddContent(0, $"Are you sure you want to cancel order '{orderIdToCancel}'?")),
            ["SubmitText"] = "Cancel Order",
            ["Color"] = Color.Error
        };

        var dialog = await DialogService.ShowAsync<OrderConfirmationDialog>("Confirm Cancellation", parameters);
        if (dialog is null) return;

        var result = await dialog.Result;
        if (!result.Canceled)
        {
            await SendCancelCommand(item.OmsIdentifier, orderIdToCancel);
        }
    }

    private async Task SendCancelCommand(string omsIdentifier, string orderId)
    {
        var targetServer = _availableOmsServers.FirstOrDefault(s => s.OmsIdentifier == omsIdentifier);
        if (targetServer is null)
        {
            Snackbar.Add($"Cannot cancel order, not connected to OMS: {omsIdentifier}", Severity.Error);
            return;
        }

        var command = new ManualOrderCancelCommand(new ManualOrderCancelPayload(orderId));
        await OmsConnector.SendCommandAsync(targetServer, command);
        Snackbar.Add($"Cancel command sent for order {orderId}.", Severity.Warning);
    }

    private IEnumerable<ManualOrderDisplayItem> GetDisplayOrders()
    {
        // Get all manual orders from all connected OMS servers from the cache.
        var allManualOrders = OrderCache.GetAllManualOrders();

        // Transform the reports into the display item model.
        return allManualOrders
            .Select(item => new ManualOrderDisplayItem
            {
                OmsIdentifier = item.OmsIdentifier,
                Report = item.Report
            })
            .OrderByDescending(o => o.Report.Timestamp);
    }

    private async void HandleManualOrderUpdate((string OmsIdentifier, OrderStatusReport Report) args)
    {
        if (_isDisposed) return;

        string exchangeOrderId = args.Report.ExchangeOrderId;
        if (string.IsNullOrEmpty(exchangeOrderId)) return;

        // 계층별 딕셔너리 확보
        var omsDict = _activeManualOrdersByOms.GetOrAdd(args.OmsIdentifier,
            _ => new ConcurrentDictionary<string, ManualOrderDisplayItem>());

        if (IsActiveOrder(args.Report))
        {
            // 활성 주문이면 래퍼 객체 생성 또는 업데이트
            var displayItem = new ManualOrderDisplayItem
            {
                OmsIdentifier = args.OmsIdentifier,
                Report = args.Report
            };
            omsDict[exchangeOrderId] = displayItem;
        }
        else
        {
            // 종료된 주문은 제거
            omsDict.TryRemove(exchangeOrderId, out _);
        }

        await InvokeAsync(StateHasChanged);
    }

    private void HandleTableRowClick(TableRowClickEventArgs<ManualOrderDisplayItem> args)
    {
        var item = args.Item;
        var report = item.Report;

        // because report does not contain book info
        SelectedBookName = null;
        SelectedOmsIdentifier = item.OmsIdentifier;
        SelectedInstrument = InstrumentRepository.GetById(report.InstrumentId);

        // model update
        _model.OrderId = report.ExchangeOrderId ?? report.ClientOrderId.ToString();
        _model.OrderPrice = report.Price;
        _model.Size = report.Quantity;

        StateHasChanged();
    }
    private bool IsActiveOrder(OrderStatusReport report)
    {
        return report.Status switch
        {
            OrderStatus.Filled => false,
            OrderStatus.Cancelled => false,
            OrderStatus.Rejected => false,
            _ => true
        };
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        OrderCache.OnManualOrderUpdated -= HandleManualOrderUpdate;
        _isDisposed = true;
    }
}
