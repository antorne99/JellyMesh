namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// What a choice in the migration banner did (Q55).
/// </summary>
public enum LayoutMigrationChoiceOutcome
{
    /// <summary>The manual migration is chosen.</summary>
    Chosen,

    /// <summary>The automatic migration started, or resumed from its saved step.</summary>
    Started,

    /// <summary>The automatic migration is running.</summary>
    Busy,

    /// <summary>The choice is not allowed: no mode, or back to manual from automatic.</summary>
    NotAllowed,

    /// <summary>There is nothing to migrate.</summary>
    NothingToMigrate
}
