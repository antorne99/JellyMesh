namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// How the copy of a remote stream to a client ended.
/// </summary>
public enum RelayEnd
{
    /// <summary>The whole body went to the client.</summary>
    Completed,

    /// <summary>A HEAD request, which has no body.</summary>
    Head,

    /// <summary>The client went away.</summary>
    ClientLeft,

    /// <summary>The connection to the client broke while writing to it.</summary>
    ClientDropped,

    /// <summary>The remote sent nothing for the stall limit.</summary>
    RemoteStalled,

    /// <summary>The connection to the remote broke.</summary>
    RemoteBroke,

    /// <summary>The connection to the remote broke soon after a pause of the client, put down to the pause (row 273).</summary>
    RemoteBrokeAfterClientPause,

    /// <summary>The server of the stream is being removed.</summary>
    ServerRemoved
}
