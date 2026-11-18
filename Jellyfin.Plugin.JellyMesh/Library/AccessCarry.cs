using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// A library of the new library mode with, for each of its folders, the old libraries that held it (§10 row 438).
/// </summary>
/// <param name="Name">The name of the library.</param>
/// <param name="Id">The id of the library, or null in a preview, before it exists.</param>
/// <param name="Folders">For each folder of the library, the ids of the old libraries that held it.</param>
public sealed record AccessCarry(string Name, Guid? Id, IReadOnlyList<IReadOnlyList<Guid>> Folders);
