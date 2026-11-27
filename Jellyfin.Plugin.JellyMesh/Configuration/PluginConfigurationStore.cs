using System;
using System.Threading;
using Jellyfin.Plugin.JellyMesh.Remote;
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
    private readonly ErrorLogLimits _errorLogs;
    private PluginConfiguration _config = new();

    // After a failed read, no new attempt before this time, so that a locked database is not waited on by every request.
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
    private Exception? _loadFailure;

    // The rows in the database; null until the configuration was read from it.
    private ConfigurationRows? _saved;

    // The database generation the rows were read in: a database opened again is read again.
    private long _loadedGeneration;

    // Saves that replaced or removed a credential, and how many of them a checkpoint has since emptied from the log.
    private long _droppedCredentials;
    private long _emptiedCredentials;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfigurationStore"/> class.
    /// </summary>
    /// <param name="database">The plugin database.</param>
    /// <param name="rootSource">The federated folder set by the environment, which wins over the saved one.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock of the pause between two failed reads.</param>
    /// <param name="errorLogs">The limits of the errors in the log.</param>
    public PluginConfigurationStore(IPluginDatabase database, IFederatedRootSource rootSource, ILogger<PluginConfigurationStore> logger, TimeProvider timeProvider, ErrorLogLimits? errorLogs = null)
    {
        _errorLogs = errorLogs ?? new ErrorLogLimits();
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
        T result;
        bool reloaded;
        lock (_lock)
        {
            reloaded = EnsureLoaded();
            result = read(_config);
        }

        if (reloaded)
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        return result;
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
        var settingsChanged = false;
        try
        {
            lock (_lock)
            {
                settingsChanged = EnsureLoaded();

                // Compared with the settings in force, not the saved rows: a database with no settings saved runs the defaults.
                var before = ConfigurationRows.From(_config);
                try
                {
                    update(_config);
                    var current = ConfigurationRows.From(_config);
                    var changed = !before.SameRows(current, ConfigurationTables.Settings);
                    Save(current);
                    settingsChanged |= changed;
                }
                catch
                {
                    rollback?.Invoke(_config);
                    throw;
                }
            }
        }
        finally
        {
            if (settingsChanged)
            {
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        EmptyLogOfOldCredentials();
    }

    // Empties the database log while it may still hold a replaced or removed credential, outside the lock, since a reader
    // can make it wait. The change is saved by then, so a failure is only logged and the next save tries again.
    private void EmptyLogOfOldCredentials()
    {
        long dropped;
        lock (_lock)
        {
            dropped = _droppedCredentials;
            if (dropped == _emptiedCredentials)
            {
                return;
            }
        }

        try
        {
            if (!_database.TruncateWalAsync(CancellationToken.None).GetAwaiter().GetResult())
            {
                _logger.LogDebug("The JellyMesh database log was busy; replaced credentials are cleared from it at the next save");
                return;
            }

            lock (_lock)
            {
                _emptiedCredentials = Math.Max(_emptiedCredentials, dropped);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning("The JellyMesh database log could not be emptied of replaced credentials; the next save tries again: {Error}{Detail:l}", exception.GetType().Name, _errorLogs.Local.Of(exception));
        }
    }

    // Reads the configuration once, and again when the database was opened again; true when that changed the settings.
    // Until the first read nothing is handed out, not even the defaults; after it a failed read keeps what was loaded.
    private bool EnsureLoaded()
    {
        var loaded = _saved is not null;
        if (loaded && _database.Generation == _loadedGeneration)
        {
            return false;
        }

        if (!loaded && _database.Health.Suspended)
        {
            throw new StoreSuspendedException("The JellyMesh database is suspended, so its settings are not known.");
        }

        if (_timeProvider.GetUtcNow() < _retryAfter)
        {
            return loaded ? false : throw new StoreSuspendedException("The JellyMesh settings could not be read from the database.", _loadFailure!);
        }

        var before = loaded ? ConfigurationRows.From(_config) : null;
        try
        {
            Load();
            _loadFailure = null;
            return before is not null && !before.SameRows(ConfigurationRows.From(_config), ConfigurationTables.Settings);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (_loadFailure is null && loaded)
            {
                _logger.LogWarning("The JellyMesh settings could not be read again from the database; the loaded ones stay until they can: {Error}{Detail:l}", exception.GetType().Name, _errorLogs.Local.Of(exception));
            }
            else if (_loadFailure is null)
            {
                _logger.LogWarning("The JellyMesh settings could not be read from the database; JellyMesh waits until they can: {Error}{Detail:l}", exception.GetType().Name, _errorLogs.Local.Of(exception));
            }

            _loadFailure = exception;
            _retryAfter = _timeProvider.GetUtcNow() + RetryPause;
            return loaded ? false : throw new StoreSuspendedException("The JellyMesh settings could not be read from the database.", exception);
        }
    }

    private void Load()
    {
        var (rows, generation) = _database.ReadAsync(
            (connection, transaction) => (ConfigurationTables.Read(connection, transaction), _database.Generation),
            CancellationToken.None).GetAwaiter().GetResult();
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
        _loadedGeneration = generation;
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
            _logger.LogWarning("The federated folder of {Variable} could not be saved; it is used anyway: {Error}{Detail:l}", EnvironmentFederatedRootSource.VariableName, exception.GetType().Name, _errorLogs.Local.Of(exception));
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

        _database.WriteAsync(
            (connection, transaction) =>
            {
                // The difference holds only for the rows it was made from, which a restore has replaced.
                if (_database.Generation != _loadedGeneration)
                {
                    throw new StoreSuspendedException("The JellyMesh database was opened again while the configuration was being changed.");
                }

                ConfigurationTables.Write(connection, transaction, saved, current);
            },
            CancellationToken.None).GetAwaiter().GetResult();
        _saved = current;
        if (saved.DropsCredentialsIn(current))
        {
            _droppedCredentials++;
        }
    }
}
