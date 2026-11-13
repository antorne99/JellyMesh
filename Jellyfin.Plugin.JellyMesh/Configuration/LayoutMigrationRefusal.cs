using Jellyfin.Plugin.JellyMesh.Library;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// Why a choice in the migration banner was refused.
/// </summary>
/// <param name="Outcome">The reason.</param>
/// <param name="Message">The reason for the admin.</param>
public sealed record LayoutMigrationRefusal(LayoutMigrationChoiceOutcome Outcome, string Message);
