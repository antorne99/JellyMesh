namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// The answer to what a merge would do.
/// </summary>
/// <param name="Outcome">What a merge of these titles would answer now.</param>
/// <param name="Forecast">What would play, null if the merge would be refused.</param>
public sealed record MergePreview(ManualMergeOutcome Outcome, MergeForecast? Forecast);
