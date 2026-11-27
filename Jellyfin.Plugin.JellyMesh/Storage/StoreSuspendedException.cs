using System;

namespace Jellyfin.Plugin.JellyMesh.Storage;

/// <summary>
/// A read or a write was asked of the plugin database while it is not healthy.
/// </summary>
public sealed class StoreSuspendedException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StoreSuspendedException"/> class.
    /// </summary>
    public StoreSuspendedException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StoreSuspendedException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public StoreSuspendedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StoreSuspendedException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public StoreSuspendedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
