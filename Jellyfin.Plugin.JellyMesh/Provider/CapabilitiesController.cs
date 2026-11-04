using System.Net.Mime;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// Tells a connected server that it talks to JellyMesh. Any other user gets the 404 of a stock server: this server offers
/// them nothing beyond the stock API, so that is what they should use.
/// </summary>
[ApiController]
[Route("JellyMesh/Capabilities")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public class CapabilitiesController : ControllerBase
{
    private readonly PeerDirectory _peers;

    /// <summary>
    /// Initializes a new instance of the <see cref="CapabilitiesController"/> class.
    /// </summary>
    /// <param name="peers">The peers.</param>
    public CapabilitiesController(PeerDirectory peers)
    {
        _peers = peers;
    }

    /// <summary>
    /// Gets the protocol version.
    /// </summary>
    /// <returns>The capabilities.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProviderCapabilities>> GetCapabilities()
    {
        return await _peers.FindCallerAsync(HttpContext).ConfigureAwait(false) is null
            ? NotFound()
            : new ProviderCapabilities(ProviderProtocol.Version);
    }
}
