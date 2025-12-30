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
    public string BookName { get; set; } = string.Empty;
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
    // 구조: OmsIdentifier (string) -> BookName (string) -> ExchangeOrderId (string) -> Order객체
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentDictionary<string, ManualOrderDisplayItem>>> _activeManualOrdersByOms = new();

    protected override void OnInitialized()
    {
        // Load initial data for selectors
        _availableOmsServers = Configuration.GetSection("oms").Get<List<OmsServerConfig>>() ?? new List<OmsServerConfig>();
        _availableInstruments = InstrumentRepository.GetAll().ToList();
        // Subscribe to events
        // TO-DO=> make OrderCache handle orders in instances vs manual
        // OrderCache.OnManualOrderUpdated += HandleManualOrderUpdate;
    }

    protected override void OnParametersSet()
    {
        // When OMS selection changes, update the list of available book names
        if (!string.IsNullOrEmpty(SelectedOmsIdentifier))
        {
            _availableBookNames = BookCache.GetBookNames(SelectedOmsIdentifier).ToList();
            // _manualOrders = OrderCache.GetManualOrders(SelectedOmsIdentifier);
        }
        else
        {
            _availableBookNames.Clear();
            // _manualOrders = Enumerable.Empty<OrderStatusReport>();
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

    private async Task HandleSubmit()
    {
        await _form.Validate();
        if (!_form.IsValid) return;

        var parameters = new DialogParameters<OrderConfirmationDialog> // Use generic version
        {
            // Pass parameters via properties
            { x => x.Oms, SelectedOmsIdentifier },
            { x => x.Instrument, SelectedInstrument?.Symbol },
            { x => x.Book, SelectedBookName },
            { x => x.OrderPrice, _model.OrderPrice },
            { x => x.Size, _model.Size },
            { x => x.PostOnly, _model.PostOnly }
        };

        // --- MODIFICATION ---
        // Use ShowAsync instead of Show.
        // ShowAsync returns the dialog reference directly.
        var dialog = await DialogService.ShowAsync<OrderConfirmationDialog>("Confirm Manual Order", parameters);

        // ShowAsync can theoretically return null if it fails to create the dialog,
        // so a null check is good practice.
        if (dialog is null) return;

        var result = await dialog.Result;
        // --- END MODIFICATION ---

        // The rest of the logic is the same.
        if (!result.Canceled)
        {
            await SendOrderCommand();
        }
    }

    private async Task SendOrderCommand()
    {
        var connectedServers = OmsConnector.GetConnectedServers().FirstOrDefault(s => s.OmsIdentifier == SelectedOmsIdentifier);
        if (connectedServers is null || SelectedInstrument is null || SelectedBookName is null)
        {
            var errorMessage = new MarkupString(
                "Invalid order parameters:<br />" +
                $"- Instrument: {SelectedInstrument?.Symbol ?? "Missing"}<br />" +
                $"- Book: {SelectedBookName ?? "Missing"}<br />" +
                $"- OMS: {connectedServers?.OmsIdentifier ?? "Not Connected"}"
            );

            Snackbar.Add(errorMessage, Severity.Error);
            return;
        }

        // var command = new ManualOrderCommand(
        //     InstrumentId: SelectedInstrument.InstrumentId,
        //     BookName: SelectedBookName,
        //     Size: _model.Size,
        //     PostOnly: _model.PostOnly
        // );

        // await OmsConnector.SendCommandAsync(targetServer, command);
        Snackbar.Add($"Manual order command sent to {SelectedOmsIdentifier}.", Severity.Info);

        // Reset the form after submission
        _model = new ManualOrderModel();
        await _form.ResetAsync();
    }

    private void HandleCancel()
    {
        StateHasChanged();
    }

    private IEnumerable<ManualOrderDisplayItem> GetDisplayOrders()
    {
        if (string.IsNullOrEmpty(SelectedOmsIdentifier))
            return Enumerable.Empty<ManualOrderDisplayItem>();

        if (_activeManualOrdersByOms.TryGetValue(SelectedOmsIdentifier, out var omsDict))
        {
            // 중첩된 딕셔너리의 모든 래퍼 객체들을 하나의 리스트로 합침
            return omsDict.Values
                          .SelectMany(bookDict => bookDict.Values)
                          .OrderByDescending(o => o.Report.Timestamp);
        }
        return Enumerable.Empty<ManualOrderDisplayItem>();
    }

    private async void HandleManualOrderUpdate((string OmsIdentifier, string BookName, OrderStatusReport Report) args)
    {
        string exchangeOrderId = args.Report.ExchangeOrderId;
        if (string.IsNullOrEmpty(exchangeOrderId)) return;

        // 계층별 딕셔너리 확보
        var omsDict = _activeManualOrdersByOms.GetOrAdd(args.OmsIdentifier,
            _ => new ConcurrentDictionary<string, ConcurrentDictionary<string, ManualOrderDisplayItem>>());

        var bookDict = omsDict.GetOrAdd(args.BookName,
            _ => new ConcurrentDictionary<string, ManualOrderDisplayItem>());

        if (IsActiveOrder(args.Report))
        {
            // 활성 주문이면 래퍼 객체 생성 또는 업데이트
            var displayItem = new ManualOrderDisplayItem
            {
                OmsIdentifier = args.OmsIdentifier,
                BookName = args.BookName,
                Report = args.Report
            };
            bookDict[exchangeOrderId] = displayItem;
        }
        else
        {
            // 종료된 주문은 제거
            bookDict.TryRemove(exchangeOrderId, out _);
        }

        if (args.OmsIdentifier == SelectedOmsIdentifier)
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private void HandleTableRowClick(TableRowClickEventArgs<ManualOrderDisplayItem> args)
    {
        var item = args.Item;
        var report = item.Report;

        SelectedOmsIdentifier = item.OmsIdentifier;
        SelectedBookName = item.BookName;
        SelectedInstrument = InstrumentRepository.GetById(report.InstrumentId);

        // 모델 업데이트
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
        // OrderCache.OnManualOrderUpdated -= HandleManualOrderUpdate;
    }
}
