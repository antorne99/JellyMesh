using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// The target of a <c>.strm</c> file. The server itself calls it, with no user identity, for the local fallback, so a
/// user check cannot apply: only a connection from the server itself is served.
/// </summary>
[ApiController]
[Route("JellyMesh/Play")]
[AllowAnonymous]
public class StrmController : ControllerBase
{
    private readonly IStubRegistry _stubs;
    private readonly PlaybackCoordinator _coordinator;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmController"/> class.
    /// </summary>
    /// <param name="stubs">The stubs.</param>
    /// <param name="coordinator">The coordinator.</param>
    public StrmController(IStubRegistry stubs, PlaybackCoordinator coordinator)
    {
        _stubs = stubs;
        _coordinator = coordinator;
    }

    /// <summary>
    /// Reads the remote file of a stub, with <c>Range</c>.
    /// </summary>
    /// <param name="stubId">The stub.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The file.</returns>
    [HttpGet("{stubId}")]
    [HttpHead("{stubId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> GetFile([FromRoute] string stubId, CancellationToken cancellationToken)
    {
        if (!LoopbackGuard.IsLocalRequest(HttpContext))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (_stubs.Find(stubId) is not { } stub)
        {
            return NotFound();
        }

        var method = HttpMethods.IsHead(Request.Method) ? HttpMethod.Head : HttpMethod.Get;
        var range = Request.Headers.Range.ToString();
        var opened = await _coordinator.OpenFileAsync(stub, method, string.IsNullOrEmpty(range) ? null : range, cancellationToken).ConfigureAwait(false);
        if (opened is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        using (opened)
        {
            await StreamRelay.CopyAsync(opened, HttpContext, cancellationToken).ConfigureAwait(false);
        }

        return new EmptyResult();
    }
}
