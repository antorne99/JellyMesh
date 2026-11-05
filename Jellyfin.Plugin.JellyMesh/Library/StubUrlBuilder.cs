using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Security;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Builds the content of a <c>.strm</c>: an address of this server with the signature of a local key, no token and nothing
/// about the remotes.
/// </summary>
public sealed class StubUrlBuilder
{
    private readonly IServerApplicationHost _host;
    private readonly IConfigurationManager _configuration;
    private readonly StrmSigner _signer;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubUrlBuilder"/> class.
    /// </summary>
    /// <param name="host">The server host.</param>
    /// <param name="configuration">The server configuration.</param>
    /// <param name="signer">The signer of the URLs.</param>
    public StubUrlBuilder(IServerApplicationHost host, IConfigurationManager configuration, StrmSigner signer)
    {
        _host = host;
        _configuration = configuration;
        _signer = signer;
    }

    /// <summary>
    /// Builds the URL of a stub.
    /// </summary>
    /// <param name="stubId">The stub.</param>
    /// <returns>The URL.</returns>
    public string Build(string stubId)
        => PlaybackRoutes.StrmUrl(_host.HttpPort, _configuration.GetNetworkConfiguration().BaseUrl, stubId, _signer.Sign(stubId));
}
