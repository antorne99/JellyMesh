namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// Why a remote operation failed.
/// </summary>
public enum RemoteFailure
{
    /// <summary>
    /// No failure.
    /// </summary>
    None,

    /// <summary>
    /// The key is not usable, so remote work is suspended. No request was sent.
    /// </summary>
    Suspended,

    /// <summary>
    /// The server is not configured here.
    /// </summary>
    UnknownServer,

    /// <summary>
    /// The server name or URL is not acceptable.
    /// </summary>
    InvalidServer,

    /// <summary>
    /// The connection failed.
    /// </summary>
    Unreachable,

    /// <summary>
    /// The server did not answer in time.
    /// </summary>
    Timeout,

    /// <summary>
    /// The server refused the credentials or the token. The only failure that calls for a new login.
    /// </summary>
    AuthenticationFailed,

    /// <summary>
    /// The server knows the user and does not allow this call, as with a route closed to peers. A new login would change
    /// nothing, and it would cost the session in use: a login on the same device invalidates the previous token.
    /// </summary>
    Forbidden,

    /// <summary>
    /// The account is an administrator. Only a dedicated non-admin user is allowed.
    /// </summary>
    AdministratorAccount,

    /// <summary>
    /// The server redirected in a way that is not followed, such as to another host.
    /// </summary>
    RedirectRefused,

    /// <summary>
    /// The server answered with an error status.
    /// </summary>
    ServerError,

    /// <summary>
    /// The answer is not what a Jellyfin server sends.
    /// </summary>
    InvalidResponse,

    /// <summary>
    /// The answer exceeds what is accepted from a remote.
    /// </summary>
    ResponseTooLarge,

    /// <summary>
    /// A library chosen for import is no longer visible to the dedicated user.
    /// </summary>
    LibraryUnavailable,

    /// <summary>
    /// The catalog kept changing while it was being read, so no complete copy of it was obtained.
    /// </summary>
    CatalogChanged,

    /// <summary>
    /// A JellyMesh remote refused a stream because this server is at its limit there. It says nothing about the health
    /// of the remote, and a stock server never answers this.
    /// </summary>
    StreamLimitReached,

    /// <summary>
    /// This server already has as many playback calls or streams open on that remote as it allows itself. Says nothing
    /// about the health of the remote, and no request was sent.
    /// </summary>
    Busy
}
