using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// What an answer must look like before anything walks it. JSON puts a null wherever a remote likes, and everything after
/// the parsing takes the lists as they are declared: one null in one item would otherwise stop the sync of every server.
/// </summary>
internal static class RemoteAnswers
{
    /// <summary>
    /// Checks an item of a catalog.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>Whether it can be used.</returns>
    public static bool IsWellFormed(RemoteItem? item)
    {
        return item is { Id.Length: > 0 }
            && HasNoNull(item.Taglines)
            && HasNoNull(item.Genres)
            && HasNoNull(item.Tags)
            && HasNoNull(item.BackdropImageTags)
            && HasNoNull(item.Studios)
            && HasNoNull(item.People)
            && HasNoNull(item.Chapters)
            && (item.ImageTags?.Values.All(tag => tag is not null) ?? true)
            && (item.MediaSources?.All(source => source is { Id.Length: > 0 } && HasNoNull(source.MediaStreams)) ?? true);
    }

    /// <summary>
    /// Checks an entry of the change feed.
    /// </summary>
    /// <param name="change">The entry.</param>
    /// <returns>Whether it can be used.</returns>
    public static bool IsWellFormed(RemoteChange? change)
    {
        return change is { Id.Length: > 0, Type: not null, LibraryIds: not null } && HasNoNull(change.LibraryIds);
    }

    /// <summary>
    /// Checks a <c>PlaybackInfo</c>.
    /// </summary>
    /// <param name="info">The answer.</param>
    /// <returns>Whether it can be used.</returns>
    public static bool IsWellFormed(PlaybackInfoResponse? info)
    {
        return info is { MediaSources: not null }
            && (info.PlaySessionId is null || CanGoInAHeader(info.PlaySessionId))
            && info.MediaSources.All(source => source is not null && HasNoNull(source.MediaStreams));
    }

    /// <summary>
    /// Checks a value of a remote that this server sends back in a header, such as a token. A line break there would be a
    /// header of its own.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>Whether it is plain text.</returns>
    public static bool CanGoInAHeader(string? value)
    {
        return !string.IsNullOrEmpty(value) && value.All(character => character is > ' ' and < '\u007f');
    }

    private static bool HasNoNull<T>(IEnumerable<T>? list)
        where T : class?
    {
        return list?.All(entry => entry is not null) ?? true;
    }
}
