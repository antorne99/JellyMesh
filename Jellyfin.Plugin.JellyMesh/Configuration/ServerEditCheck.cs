using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The answer of the check of an edit, for its confirmation (row 432). Never holds a token or a password.
/// </summary>
/// <param name="ServerName">The name the remote gives itself. Untrusted text.</param>
/// <param name="Libraries">The libraries the dedicated user sees, those to import selected.</param>
/// <param name="Consequences">The consequences of the save; the confirmation lists only those present.</param>
public sealed record ServerEditCheck(string ServerName, IReadOnlyList<LibraryOption> Libraries, ServerEditConsequences Consequences)
{
    /// <summary>
    /// Gets a value indicating whether the remote did not answer: only the name and the library mode can change, and the
    /// libraries were not read (D28).
    /// </summary>
    public bool RemoteOffline { get; init; }

    /// <summary>
    /// Gets why the remote could not be read for an edit that did not need it; <see cref="RemoteFailure.None"/> otherwise.
    /// </summary>
    public RemoteFailure RemoteWarning { get; init; }
}
