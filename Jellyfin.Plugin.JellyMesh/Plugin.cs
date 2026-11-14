using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh;

/// <summary>
/// The JellyMesh plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly Lock _configurationLock = new();
    private readonly string? _federatedRoot;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{Plugin}"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
        : this(applicationPaths, xmlSerializer, logger, new EnvironmentFederatedRootSource())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class with the source of the federated folder.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{Plugin}"/> interface.</param>
    /// <param name="rootSource">The source of the federated folder.</param>
    internal Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger, IFederatedRootSource rootSource)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        foreach (var setting in Configuration.Normalize())
        {
            logger.LogWarning("Saved setting {Setting} is out of range: {Saved} runs as {Applied}. Saving the settings keeps the new value", setting.Name, setting.Saved, setting.Applied);
        }

        var root = rootSource.GetFederatedRoot();
        _federatedRoot = PluginConfiguration.FederatedRootOf(root);
        if (_federatedRoot is null)
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                logger.LogWarning("{Variable} is ignored: {Value} is not an absolute path other than the filesystem root", EnvironmentFederatedRootSource.VariableName, root);
            }
        }
        else if (!string.Equals(_federatedRoot, Configuration.FederatedFolderPath, StringComparison.Ordinal))
        {
            Configuration.FederatedFolderPath = _federatedRoot;
            SaveConfiguration();
        }
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
    /// of that JSON, so the ones in force are kept, and a folder set outside the page stays.
    /// </summary>
    /// <param name="configuration">The configuration to apply.</param>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var incoming = (PluginConfiguration)configuration;
        incoming.FederatedFolderPath = _federatedRoot ?? incoming.FederatedFolderPath;
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
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
                EnableInMainMenu = true,
                MenuIcon = "hub"
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

    /// <summary>
    /// Changes the configuration under the configuration lock and saves it; on a failure, rolls the change back under the same lock.
    /// </summary>
    /// <param name="update">The change to apply.</param>
    /// <param name="rollback">Puts the configuration back as saved.</param>
    internal void ChangeConfiguration(Action<PluginConfiguration> update, Action<PluginConfiguration> rollback)
    {
        lock (_configurationLock)
        {
            try
            {
                update(Configuration);
                SaveConfiguration();
            }
            catch
            {
                rollback(Configuration);
                throw;
            }
        }
    }
}
