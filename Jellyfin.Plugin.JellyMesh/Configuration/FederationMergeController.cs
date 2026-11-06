using System;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// Merges film titles by hand and undoes it (decision 20), for administrators only.
/// </summary>
[ApiController]
[Route("JellyMesh/Admin/Merges")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class FederationMergeController : ControllerBase
{
    private readonly ManualMergeService _merges;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationMergeController"/> class.
    /// </summary>
    /// <param name="merges">The merges.</param>
    public FederationMergeController(ManualMergeService merges)
    {
        _merges = merges;
    }

    /// <summary>
    /// Gets the titles that can be merged and the merges made.
    /// </summary>
    /// <returns>The list.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ManualMergeList> GetMerges() => _merges.GetList();

    /// <summary>
    /// Merges titles of one case.
    /// </summary>
    /// <param name="request">The titles.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Nothing.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Merge([FromBody] MergeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var outcome = await _merges.MergeAsync(request.WorkKeys, cancellationToken).ConfigureAwait(false);
        return outcome == ManualMergeOutcome.Merged ? NoContent() : Refusal(outcome);
    }

    /// <summary>
    /// Tells what would play if titles of one case were merged, changing nothing. It refuses what a merge would refuse.
    /// </summary>
    /// <param name="request">The titles.</param>
    /// <returns>The forecast.</returns>
    [HttpPost("Preview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<MergeForecast> Preview([FromBody] MergeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var preview = _merges.Preview(request.WorkKeys);
        return preview.Forecast is { } forecast ? forecast : Refusal(preview.Outcome);
    }

    /// <summary>
    /// Undoes a merge.
    /// </summary>
    /// <param name="mergeId">The merge.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Nothing.</returns>
    [HttpDelete("{mergeId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Undo([FromRoute] Guid mergeId, CancellationToken cancellationToken)
    {
        return await _merges.UndoAsync(mergeId, cancellationToken).ConfigureAwait(false)
            ? NoContent()
            : NotFound(Failure("This merge no longer exists."));
    }

    private ObjectResult Refusal(ManualMergeOutcome outcome) => outcome switch
    {
        ManualMergeOutcome.TooFewTitles => BadRequest(Failure("Select at least two titles of one group.")),
        ManualMergeOutcome.TwoLocalTitles => BadRequest(Failure("Two local films cannot be merged: JellyMesh does not change local files. Select at most one title with a local copy.")),
        _ => Conflict(Failure("The list changed. Check it again, then merge."))
    };

    private static FailureResponse Failure(string message) => new(RemoteFailure.None, message);
}
