using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyMesh;

/// <summary>
/// The JellyMesh plugin. It has no Jellyfin configuration: settings live in the plugin database, so the stock
/// <c>/Plugins/{id}/Configuration</c> answers 404 (§3.2).
/// </summary>
public class Plugin : BasePlugin, IHasWebPages
{
    /// <summary>
    /// The name of the data folder under the plugins folder, where 0.5 keeps it. It must never equal <see cref="Name"/>:
    /// Jellyfin would take it for an install of the plugin and delete the real one (F23).
    /// </summary>
    internal const string DataFolderName = "Jellyfin.Plugin.JellyMesh";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);

        // Only BasePlugin of T sets these; without them Jellyfin fails to load the plugin (F23).
        var assembly = GetType().Assembly;
        SetAttributes(assembly.Location, Path.Combine(applicationPaths.PluginsPath, DataFolderName), assembly.GetName().Version!);
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "JellyMesh";

    /// <inheritdoc />
    public override string Description => "Federates multiple Jellyfin servers into one library.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("28c43ea1-fb5a-4b3a-8e6e-30aa3245c653");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
                EnableInMainMenu = true,
                MenuIcon = "hub"
            }
        ];
    }
}
