using System;
using System.Reflection;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// <see cref="ILibraryViewIds"/> read from the running library manager, which has <c>GetShadowViewId</c> and
/// <c>GetNamedViewId</c> from Jellyfin 12.2; the 12.1 packages the plugin builds against do not declare them.
/// </summary>
public sealed class LibraryViewIds : ILibraryViewIds
{
    private readonly ILibraryManager _library;
    private readonly MethodInfo? _shadow;
    private readonly MethodInfo? _named;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryViewIds"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    public LibraryViewIds(ILibraryManager library)
    {
        _library = library;
        var type = library.GetType();
        _shadow = Find(type, "GetShadowViewId", [typeof(string), typeof(Guid), typeof(CollectionType?)]);
        _named = Find(type, "GetNamedViewId", [typeof(User), typeof(Guid), typeof(CollectionType?)]);
    }

    /// <inheritdoc />
    public bool IsAvailable => _shadow is not null && _named is not null;

    /// <inheritdoc />
    public Guid ShadowViewId(string name, Guid parentId, CollectionType? viewType) => Invoke(_shadow, name, parentId, viewType);

    /// <inheritdoc />
    public Guid NamedViewId(User user, Guid parentId, CollectionType? viewType) => Invoke(_named, user, parentId, viewType);

    private static MethodInfo? Find(Type type, string name, Type[] parameters)
    {
        var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, parameters);
        return method?.ReturnType == typeof(Guid) ? method : null;
    }

    private Guid Invoke(MethodInfo? method, params object?[] arguments) =>
        method?.Invoke(_library, arguments) is Guid id ? id : throw new NotSupportedException("The running server has no view ids.");
}
