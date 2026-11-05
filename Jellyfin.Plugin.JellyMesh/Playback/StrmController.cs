using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// The target of a <c>.strm</c> file. The server itself calls it, with no user identity, for the local fallback, so a
/// user check cannot apply: only a connection from the server itself, with the signature of the URL it wrote, is served.
/// The signature can be read by users who can list the item, so the loopback check is what keeps a leak harmless.
/// </summary>
[ApiController]
[Route("JellyMesh/Play")]
[AllowAnonymous]
public class StrmController : ControllerBase
{
    private readonly IStubRegistry _stubs;
    private readonly PlaybackCoordinator _coordinator;
    private readonly StrmSigner _signer;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmController"/> class.
    /// </summary>
    /// <param name="stubs">The stubs.</param>
    /// <param name="coordinator">The coordinator.</param>
    /// <param name="signer">The signer of the URLs.</param>
    public StrmController(IStubRegistry stubs, PlaybackCoordinator coordinator, StrmSigner signer)
    {
        _stubs = stubs;
        _coordinator = coordinator;
        _signer = signer;
    }

    /// <summary>
    /// Reads the remote file of a stub, with <c>Range</c>.
    /// </summary>
    /// <param name="stubId">The stub.</param>
    /// <param name="signature">The signature in the URL.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The file.</returns>
    [HttpGet("{stubId}")]
    [HttpHead("{stubId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> GetFile([FromRoute] string stubId, [FromQuery(Name = PlaybackRoutes.SignatureParameter)] string? signature, CancellationToken cancellationToken)
    {
        // Before the lookup, so that an answer never tells which stubs exist.
        if (!LoopbackGuard.IsLocalRequest(HttpContext) || !_signer.IsValid(stubId, signature))
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
            await StreamRelay.CopyAsync(opened, HttpContext, _coordinator.StallLimit, cancellationToken).ConfigureAwait(false);
        }

        return new EmptyResult();
    }
}
