using System;
using System.IO;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// The stream was ended on this side because its server is being removed (413), as opposed to a remote that broke.
/// </summary>
public sealed class RemoteStreamClosedException : IOException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteStreamClosedException"/> class.
    /// </summary>
    public RemoteStreamClosedException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteStreamClosedException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public RemoteStreamClosedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteStreamClosedException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The end of the copy.</param>
    public RemoteStreamClosedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
