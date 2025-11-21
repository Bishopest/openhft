using System;
using Microsoft.AspNetCore.Components;
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

    protected override void OnInitialized()
    {
        _bookNames = BookCache.GetBookNames();
        BookCache.OnBookUpdated += OnBookCacheUpdated;
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