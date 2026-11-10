using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The outcome of removing many stubs.
/// </summary>
/// <param name="State">The registry without what was removed.</param>
/// <param name="Removed">How many stubs went.</param>
/// <param name="Failures">What stopped the others, which stay in the registry.</param>
public sealed record StubRemoval(StubState State, int Removed, IReadOnlyList<Exception> Failures);
