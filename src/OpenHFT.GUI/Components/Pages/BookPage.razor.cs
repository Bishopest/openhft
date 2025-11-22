using System;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using OpenHFT.Core.Interfaces;
using OpenHFT.GUI.Services;

namespace OpenHFT.GUI.Components.Pages;

public partial class BookPage : ComponentBase, IDisposable
{
    [Inject]
    private IBookCacheService BookCache { get; set; } = default!;
    [Inject]
    private IInstrumentRepository InstrumentRepository { get; set; } = default!;

    private IEnumerable<string> _bookNames = Enumerable.Empty<string>();
    private HashSet<string> _expandedBooks = new();

    protected override void OnInitialized()
    {
        _bookNames = BookCache.GetBookNames();
        BookCache.OnBookUpdated += OnBookCacheUpdated;
    }
    private void OnRowClick(TableRowClickEventArgs<string> args)
    {
        if (_expandedBooks.Contains(args.Item))
        {
            _expandedBooks.Remove(args.Item);
        }
        else
        {
            _expandedBooks.Add(args.Item);
        }
    }

    private async void OnBookCacheUpdated()
    {
        _bookNames = BookCache.GetBookNames();
        await InvokeAsync(StateHasChanged);
    }

    protected string GetSymbolFromId(int instrumentId)
    {
        return InstrumentRepository.GetById(instrumentId)?.Symbol ?? "Unknown";
    }

    public void Dispose()
    {
        BookCache.OnBookUpdated -= OnBookCacheUpdated;
    }
}