namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// Tells a sync whether anything that the materialization and the version links read changed since the last sync that
/// completed them: the index, the servers, the settings that shape the stubs, the local library and the stub registry
/// (§10 row 386).
/// </summary>
public interface IUnchangedSyncGate
{
    /// <summary>
    /// Reads what the materialization and the version links depend on now.
    /// </summary>
    /// <returns>The inputs.</returns>
    SyncInputs Capture();

    /// <summary>
    /// Tells whether the inputs are those of the last completed run.
    /// </summary>
    /// <param name="inputs">Inputs from <see cref="Capture"/>.</param>
    /// <returns>True when nothing changed.</returns>
    bool IsUnchanged(SyncInputs inputs);

    /// <summary>
    /// Keeps the inputs of a run that completed, to compare the next ones with.
    /// </summary>
    /// <param name="inputs">The inputs.</param>
    void Remember(SyncInputs inputs);

    /// <summary>
    /// Drops the inputs kept, so that the next sync runs whatever it finds.
    /// </summary>
    void Forget();
}
