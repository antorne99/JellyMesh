using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// Feeds the <see cref="ChangeJournal"/> from the events of the library. A stub never enters it (§4.4).
/// </summary>
public sealed class ChangeFeedRecorder : IHostedService
{
    private readonly ILibraryManager _library;
    private readonly ProviderCatalog _catalog;
    private readonly ChangeJournal _journal;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChangeFeedRecorder"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="catalog">The catalog.</param>
    /// <param name="journal">The journal.</param>
    public ChangeFeedRecorder(ILibraryManager library, ProviderCatalog catalog, ChangeJournal journal)
    {
        _library = library;
        _catalog = catalog;
        _journal = journal;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded += OnItemChanged;
        _library.ItemUpdated += OnItemChanged;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded -= OnItemChanged;
        _library.ItemUpdated -= OnItemChanged;
        return Task.CompletedTask;
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        if (ProviderCatalog.IsCatalogItem(e.Item) && !_catalog.IsFederated(e.Item))
        {
            _journal.Record(e.Item.Id);
        }
    }
}
