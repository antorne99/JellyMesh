namespace Jellyfin.Plugin.JellyMesh.Tests;

/// <summary>The remote library ids the tests use, in the 32-digit form Jellyfin gives.</summary>
internal static class TestLibraries
{
    /// <summary>The library of films.</summary>
    public const string Films = "f11e0000000000000000000000000001";

    /// <summary>The library of series.</summary>
    public const string Shows = "5e0e0000000000000000000000000002";

    /// <summary>A second library of films.</summary>
    public const string FilmsB = "f11e000000000000000000000000000b";

    /// <summary>A third library of films.</summary>
    public const string FilmsC = "f11e000000000000000000000000000c";

    /// <summary>A second library of series.</summary>
    public const string ShowsB = "5e0e000000000000000000000000000b";

    /// <summary>A third library of series.</summary>
    public const string ShowsC = "5e0e000000000000000000000000000c";

    /// <summary>A library of music.</summary>
    public const string Music = "a0d1000000000000000000000000000a";

    /// <summary>A library of music videos.</summary>
    public const string MusicVideos = "c11b000000000000000000000000000a";

    /// <summary>A library of home videos and photos.</summary>
    public const string HomeVideos = "40e0000000000000000000000000000a";

    /// <summary>A library of books.</summary>
    public const string Books = "b00c000000000000000000000000000a";

    /// <summary>A library with no type, of films and series.</summary>
    public const string Mixed = "a1c0000000000000000000000000000a";
}
