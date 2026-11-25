using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// Bounds the names and artists a remote sends (row 529). Local names are not bounded.
/// </summary>
internal static class RemoteName
{
    /// <summary>
    /// Longest name kept, in UTF-16 units.
    /// </summary>
    public const int MaxLength = 1000;

    /// <summary>
    /// Cuts a name to <see cref="MaxLength"/>, one unit earlier when the cut would split a surrogate pair.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The name, unchanged when it is not longer.</returns>
    [return: NotNullIfNotNull(nameof(name))]
    public static string? Cut(string? name)
    {
        if (name is not { Length: > MaxLength })
        {
            return name;
        }

        return name[..(char.IsHighSurrogate(name[MaxLength - 1]) ? MaxLength - 1 : MaxLength)];
    }

    /// <summary>
    /// Tells whether a name may have been cut, so that another one could begin the same way.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>Whether it is as long as a cut name, or longer.</returns>
    public static bool MayBeCut(string? name) => name is { Length: >= MaxLength - 1 };
}
