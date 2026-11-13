using System;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// A library rename whose user lists may still name the old id, kept until every user is rewritten (§10 row 290).
/// </summary>
/// <param name="OldName">The name before the rename.</param>
/// <param name="NewName">The name after the rename.</param>
/// <param name="OldId">The id before the rename.</param>
/// <param name="NewId">The id after the rename.</param>
/// <param name="Type">The library type, for the view ids of 12.2.</param>
/// <param name="TemporaryName">The folder a change of case passes through, or null.</param>
public sealed record LibraryRenameRecord(string OldName, string NewName, Guid OldId, Guid NewId, CollectionType? Type, string? TemporaryName);
