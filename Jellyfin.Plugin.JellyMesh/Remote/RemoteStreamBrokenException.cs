using System;
using System.IO;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// The connection to a remote broke while its body was being read, as opposed to the one to the client.
/// </summary>
public sealed class RemoteStreamBrokenException : IOException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteStreamBrokenException"/> class.
    /// </summary>
    public RemoteStreamBrokenException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteStreamBrokenException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public RemoteStreamBrokenException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteStreamBrokenException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The failure of the read.</param>
    public RemoteStreamBrokenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
