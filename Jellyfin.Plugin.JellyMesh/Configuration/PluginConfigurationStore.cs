using System;
using System.Threading;
using Jellyfin.Plugin.JellyMesh.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// <see cref="IPluginConfigurationStore"/> over the plugin database: the configuration is read once into memory and every
/// change that alters it is written in one transaction. No configuration XML is read or written.
/// </summary>
/// <remarks>
/// <see cref="Update(Action{PluginConfiguration})"/> waits for the database writer while it holds the configuration lock, so it
/// must never be called from inside a database write callback.
/// </remarks>
public sealed class PluginConfigurationStore : IPluginConfigurationStore
{
    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(5);

    private readonly Lock _lock = new();
    private readonly IPluginDatabase _database;
    private readonly IFederatedRootSource _rootSource;
    private readonly ILogger<PluginConfigurationStore> _logger;
    private readonly TimeProvider _timeProvider;
    private PluginConfiguration _config = new();

    // After a failed read, no new attempt before this time, so that a locked database is not waited on by every request.
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
    private Exception? _loadFailure;

    // The rows in the database; null until the configuration was read from it.
    private ConfigurationRows? _saved;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfigurationStore"/> class.
    /// </summary>
    /// <param name="database">The plugin database.</param>
    /// <param name="rootSource">The federated folder set by the environment, which wins over the saved one.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock of the pause between two failed reads.</param>
    public PluginConfigurationStore(IPluginDatabase database, IFederatedRootSource rootSource, ILogger<PluginConfigurationStore> logger, TimeProvider timeProvider)
    {
        _database = database;
        _rootSource = rootSource;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public event EventHandler? SettingsChanged;

    /// <inheritdoc />
    /// <exception cref="StoreSuspendedException">The configuration was never read from the database, so none is known.</exception>
    public T Read<T>(Func<PluginConfiguration, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_lock)
        {
            EnsureLoaded();
            return read(_config);
        }
    }

    /// <inheritdoc />
    /// <exception cref="StoreSuspendedException">The database is suspended, so nothing was changed.</exception>
    public void Update(Action<PluginConfiguration> update) => Change(update, null);

    /// <inheritdoc />
    /// <exception cref="StoreSuspendedException">The database is suspended, so nothing was changed.</exception>
    public void Update(Action<PluginConfiguration> update, Action<PluginConfiguration> rollback) => Change(update, rollback);

    private void Change(Action<PluginConfiguration> update, Action<PluginConfiguration>? rollback)
    {
        ArgumentNullException.ThrowIfNull(update);
        bool settingsChanged;
        lock (_lock)
        {
            EnsureLoaded();

            // Compared with the settings in force, not the saved rows: a database with no settings saved runs the defaults.
            var before = ConfigurationRows.From(_config);
            try
            {
                update(_config);
                var current = ConfigurationRows.From(_config);
                settingsChanged = !before.SameRows(current, ConfigurationTables.Settings);
                Save(current);
            }
            catch
            {
                rollback?.Invoke(_config);
                throw;
            }
        }

        if (settingsChanged)
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // Reads the configuration once. Until that works nothing is handed out, not even the defaults; once read it is kept.
    private void EnsureLoaded()
    {
        if (_saved is not null)
        {
            return;
        }

        if (_database.Health.Suspended)
        {
            throw new StoreSuspendedException("The JellyMesh database is suspended, so its settings are not known.");
        }

        if (_timeProvider.GetUtcNow() < _retryAfter)
        {
            throw new StoreSuspendedException("The JellyMesh settings could not be read from the database.", _loadFailure!);
        }

        try
        {
            Load();
            _loadFailure = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (_loadFailure is null)
            {
                _logger.LogWarning(exception, "The JellyMesh settings could not be read from the database; JellyMesh waits until they can");
            }

            _loadFailure = exception;
            _retryAfter = _timeProvider.GetUtcNow() + RetryPause;
            throw new StoreSuspendedException("The JellyMesh settings could not be read from the database.", exception);
        }
    }

    private void Load()
    {
        var rows = _database.ReadAsync(ConfigurationTables.Read, CancellationToken.None).GetAwaiter().GetResult();
        var config = rows.ToConfiguration();
        foreach (var setting in config.Normalize())
        {
            _logger.LogWarning("Saved setting {Setting} is out of range: {Saved} runs as {Applied}. Saving the settings keeps the new value", setting.Name, setting.Saved, setting.Applied);
        }

        var value = _rootSource.GetFederatedRoot();
        var root = PluginConfiguration.FederatedRootOf(value);
        if (root is null && !string.IsNullOrWhiteSpace(value))
        {
            _logger.LogWarning("{Variable} is ignored: {Value} is not an absolute path other than the filesystem root", EnvironmentFederatedRootSource.VariableName, value);
        }

        _config = config;
        _saved = rows;
        if (root is null || string.Equals(root, config.FederatedFolderPath, StringComparison.Ordinal))
        {
            return;
        }

        // The variable wins and is saved, so the folder stays if the variable goes.
        config.FederatedFolderPath = root;
        try
        {
            Save(ConfigurationRows.From(config));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The federated folder of {Variable} could not be saved; it is used anyway", EnvironmentFederatedRootSource.VariableName);
        }
    }

    // Writes only when a table changed, so an update that changes nothing writes nothing (D27).
    private void Save(ConfigurationRows current)
    {
        var saved = _saved!;
        if (!current.DiffersFrom(saved))
        {
            return;
        }

        _database.WriteAsync((connection, transaction) => ConfigurationTables.Write(connection, transaction, saved, current), CancellationToken.None).GetAwaiter().GetResult();
        _saved = current;
    }
}
