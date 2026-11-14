using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// The ProviderApi: what a connected server reads instead of the stock catalog. Everything is scoped here by the login of
/// the caller, so a library or an item it asks for is only a request.
/// </summary>
[ApiController]
[Route(ProviderProtocol.Route)]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public class ProviderController : ControllerBase
{
    private readonly PeerDirectory _peers;
    private readonly ProviderCatalog _catalog;
    private readonly ChangeJournal _journal;
    private readonly StreamSlots _slots;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderController"/> class.
    /// </summary>
    /// <param name="peers">The peers.</param>
    /// <param name="catalog">The catalog.</param>
    /// <param name="journal">The change journal.</param>
    /// <param name="slots">The stream slots.</param>
    public ProviderController(PeerDirectory peers, ProviderCatalog catalog, ChangeJournal journal, StreamSlots slots)
    {
        _peers = peers;
        _catalog = catalog;
        _journal = journal;
        _slots = slots;
    }

    /// <summary>
    /// Lists the libraries shared with the caller.
    /// </summary>
    /// <returns>The libraries.</returns>
    [HttpGet("Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ProviderLibrariesResponse>> GetLibraries()
    {
        if (await _peers.FindCallerAsync(HttpContext).ConfigureAwait(false) is not { } caller)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        return new ProviderLibrariesResponse(_catalog.ReadLibraries().Where(library => caller.Grant.SharedLibraryIds.Contains(library.Id)).ToList());
    }

    /// <summary>
    /// Reads a page of a shared library, or items by identifier.
    /// </summary>
    /// <param name="includeItemTypes">The item type.</param>
    /// <param name="parentId">The library.</param>
    /// <param name="ids">The items, comma separated.</param>
    /// <param name="startIndex">The index of the first item.</param>
    /// <param name="limit">The most items to return, capped at <see cref="ProviderProtocol.MaxItemsPerPage"/>.</param>
    /// <returns>The items.</returns>
    [HttpGet("Items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProviderItemsResponse>> GetItems(
        [FromQuery] RemoteItemType includeItemTypes,
        [FromQuery] Guid? parentId,
        [FromQuery] string? ids,
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = ProviderProtocol.MaxItemsPerPage)
    {
        if (await _peers.FindCallerAsync(HttpContext).ConfigureAwait(false) is not { } caller)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (startIndex < 0 || limit < 1)
        {
            return BadRequest();
        }

        if (!string.IsNullOrEmpty(ids))
        {
            return ParseIds(ids) is { Count: > 0 and <= ProviderProtocol.MaxItemsPerPage } itemIds
                ? _catalog.ReadItems(caller, itemIds, includeItemTypes)
                : BadRequest();
        }

        // Not shared is the same as not there.
        if (parentId is not { } libraryId || !caller.Grant.SharedLibraryIds.Contains(libraryId.ToString("N")))
        {
            return NotFound();
        }

        return _catalog.ReadLibrary(caller, libraryId, includeItemTypes, startIndex, Math.Min(limit, ProviderProtocol.MaxItemsPerPage));
    }

    /// <summary>
    /// Reads a page of the films of a shared library that are in a collection, with the name and TMDb ID of that collection.
    /// </summary>
    /// <param name="parentId">The library.</param>
    /// <param name="startIndex">The index of the first film.</param>
    /// <param name="limit">The most films to return, capped at <see cref="ProviderProtocol.MaxItemsPerPage"/>.</param>
    /// <returns>The films with their collections.</returns>
    [HttpGet("Collections")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProviderCollectionsResponse>> GetCollections(
        [FromQuery] Guid? parentId,
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = ProviderProtocol.MaxItemsPerPage)
    {
        if (await _peers.FindCallerAsync(HttpContext).ConfigureAwait(false) is not { } caller)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (startIndex < 0 || limit < 1)
        {
            return BadRequest();
        }

        // Not shared is the same as not there.
        if (parentId is not { } libraryId || !caller.Grant.SharedLibraryIds.Contains(libraryId.ToString("N")))
        {
            return NotFound();
        }

        return _catalog.ReadCollections(caller, libraryId, startIndex, Math.Min(limit, ProviderProtocol.MaxItemsPerPage));
    }

    /// <summary>
    /// Reads the change feed.
    /// </summary>
    /// <param name="cursor">Where to read from. Without it, the answer is the current position and a reset.</param>
    /// <param name="limit">The most changes to go through, capped at <see cref="ProviderProtocol.MaxChangesPerPage"/>.</param>
    /// <returns>The changes.</returns>
    [HttpGet("Changes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ProviderChangesResponse>> GetChanges([FromQuery] string? cursor, [FromQuery] int limit = ProviderProtocol.MaxChangesPerPage)
    {
        if (await _peers.FindCallerAsync(HttpContext).ConfigureAwait(false) is not { } caller)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (limit < 1)
        {
            return BadRequest();
        }

        var window = _journal.Read(cursor, caller.Grant.ShareKey, Math.Min(limit, ProviderProtocol.MaxChangesPerPage));
        var changes = new List<ProviderChange>();
        foreach (var itemId in window.ItemIds)
        {
            if (_catalog.FindVisible(caller, itemId) is { } item)
            {
                changes.Add(new ProviderChange(item.Id.ToString("N"), item.GetBaseItemKind().ToString(), _catalog.SharedLibrariesOf(caller.Grant, item)));
            }
        }

        return new ProviderChangesResponse(window.Reset, window.Cursor, window.HasMore, changes);
    }

    /// <summary>
    /// Keeps the slot of a play session a client of the caller is still reading.
    /// </summary>
    /// <param name="playSessionId">The play session.</param>
    /// <returns>Nothing.</returns>
    [HttpPut("Streams/{playSessionId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> RenewStream([FromRoute] string playSessionId)
    {
        if (await _peers.FindCallerAsync(HttpContext).ConfigureAwait(false) is not { } caller)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        _slots.Renew(caller.Grant.UserId, playSessionId);
        return NoContent();
    }

    /// <summary>
    /// Frees the slot of a play session the caller has finished with.
    /// </summary>
    /// <param name="playSessionId">The play session.</param>
    /// <returns>Nothing.</returns>
    [HttpDelete("Streams/{playSessionId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> ReleaseStream([FromRoute] string playSessionId)
    {
        if (await _peers.FindCallerAsync(HttpContext).ConfigureAwait(false) is not { } caller)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        _slots.Release(caller.Grant.UserId, playSessionId);
        return NoContent();
    }

    private static List<Guid>? ParseIds(string ids)
    {
        var parsed = new List<Guid>();
        foreach (var id in ids.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Guid.TryParse(id, out var value))
            {
                return null;
            }

            parsed.Add(value);
        }

        return parsed;
    }
}
