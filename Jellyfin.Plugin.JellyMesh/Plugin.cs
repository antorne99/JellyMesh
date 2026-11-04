using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellyMesh;

/// <summary>
/// The JellyMesh plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly Lock _configurationLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
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

    /// <summary>
    /// Applies a configuration saved through the stock plugin endpoint. Remote servers are not part
    /// of that JSON, so the ones in force are kept.
    /// </summary>
    /// <param name="configuration">The configuration to apply.</param>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var incoming = (PluginConfiguration)configuration;
        incoming.Validate();

        lock (_configurationLock)
        {
            incoming.AdoptServersFrom(Configuration);
            base.UpdateConfiguration(incoming);
        }
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }

    /// <summary>
    /// Reads the configuration under the configuration lock.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="read">Projects the configuration to a detached result.</param>
    /// <returns>The result of <paramref name="read"/>.</returns>
    internal T ReadConfiguration<T>(Func<PluginConfiguration, T> read)
    {
        lock (_configurationLock)
        {
            return read(Configuration);
        }
    }

    /// <summary>
    /// Changes the configuration under the configuration lock and saves it.
    /// </summary>
    /// <param name="update">The change to apply.</param>
    internal void ChangeConfiguration(Action<PluginConfiguration> update)
    {
        lock (_configurationLock)
        {
            update(Configuration);
            SaveConfiguration();
        }
    }
}
