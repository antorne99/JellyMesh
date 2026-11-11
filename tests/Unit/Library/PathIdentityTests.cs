using System;
using System.IO;
using System.Runtime.InteropServices;
using Jellyfin.Plugin.JellyMesh.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class PathIdentityTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jellymesh-paths-" + Guid.NewGuid().ToString("N"));

    public PathIdentityTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "real", "federata", "Serie", "Show"));
    }

    private string Federated => Path.Combine(_folder, "real", "federata");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    [Fact]
    public void A_link_to_the_folder_names_the_folder()
    {
        var link = Link("link", Federated);

        Assert.Equal(PathIdentity.Canonical(Federated), PathIdentity.Canonical(link));
    }

    [Fact]
    public void A_link_in_an_ancestor_names_what_is_below_the_folder()
    {
        Link("link", Federated);

        Assert.Equal(
            PathIdentity.Canonical(Path.Combine(Federated, "Serie", "Show")),
            PathIdentity.Canonical(Path.Combine(_folder, "link", "Serie", "Show")));
    }

    [Fact]
    public void A_relative_link_resolves_from_the_folder_it_is_in()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "media"));
        var link = Link(Path.Combine("media", "fed"), Path.Combine("..", "real", "federata"));

        Assert.Equal(PathIdentity.Canonical(Federated), PathIdentity.Canonical(link));
    }

    [Fact]
    public void A_chain_of_links_names_its_last_target()
    {
        var first = Link("first", Federated);
        var second = Link("second", first);

        Assert.Equal(PathIdentity.Canonical(Federated), PathIdentity.Canonical(second));
    }

    [Fact]
    public void A_parent_step_after_a_link_goes_up_from_its_target()
    {
        var link = Link("link", Federated);

        Assert.Equal(PathIdentity.Canonical(Path.Combine(_folder, "real")), PathIdentity.Canonical(link + Path.DirectorySeparatorChar + ".."));
    }

    [Fact]
    public void A_missing_path_compares_as_written()
    {
        var missing = Path.Combine(PathIdentity.Canonical(_folder), "nowhere", "Serie");

        Assert.Equal(missing, PathIdentity.Canonical(missing + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void What_is_missing_below_a_link_keeps_the_resolved_part()
    {
        var link = Link("link", Federated);

        Assert.Equal(
            Path.Combine(PathIdentity.Canonical(Federated), "Film", "New"),
            PathIdentity.Canonical(Path.Combine(link, "Film", "New")));
    }

    [Fact]
    public void A_link_to_itself_compares_as_written()
    {
        var loop = Link("loop", Path.Combine(_folder, "loop"));

        Assert.Equal(Path.Combine(loop, "Serie"), PathIdentity.Canonical(Path.Combine(loop, "Serie")));
    }

    [Fact]
    public void Two_links_to_each_other_compare_as_written()
    {
        var ping = Link("ping", Path.Combine(_folder, "pong"));
        Link("pong", ping);

        Assert.Equal(ping, PathIdentity.Canonical(ping));
    }

    [Fact]
    public void Names_in_another_unicode_form_are_the_same_name()
    {
        Assert.Equal(PathIdentity.Canonical("/nowhere/Amélie"), PathIdentity.Canonical("/nowhere/Amélie"));
    }

    [Fact]
    public void A_link_to_a_folder_with_a_decomposed_name_is_found_and_names_it_composed()
    {
        var decomposed = Path.Combine(_folder, "real", "Amélie");
        Directory.CreateDirectory(decomposed);
        var link = Link("link", decomposed);

        Assert.Equal(PathIdentity.Canonical(Path.Combine(_folder, "real")) + Path.DirectorySeparatorChar + "Amélie", PathIdentity.Canonical(link));
    }

    [Theory]
    [InlineData("/federata", "/federata", true)]
    [InlineData("/federata/Serie/Show", "/federata", true)]
    [InlineData("/anything", "/", true)]
    [InlineData("/federata-other", "/federata", false)]
    [InlineData("/media", "/media/federata", false)]
    [InlineData("/Federata/Serie", "/federata", false)]
    public void A_path_is_within_the_folder_or_below_it(string path, string folder, bool expected)
    {
        Assert.Equal(expected, PathIdentity.IsWithin(path, folder, StringComparison.Ordinal));
    }

    [Fact]
    public void Where_case_does_not_count_a_path_in_another_case_is_within()
    {
        Assert.True(PathIdentity.IsWithin("/Federata/Serie", "/federata", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Case_does_not_count_on_windows_and_macos_only()
    {
        Assert.Equal(StringComparison.OrdinalIgnoreCase, PathIdentity.ComparisonFor(OSPlatform.Windows));
        Assert.Equal(StringComparison.OrdinalIgnoreCase, PathIdentity.ComparisonFor(OSPlatform.OSX));
        Assert.Equal(StringComparison.Ordinal, PathIdentity.ComparisonFor(OSPlatform.Linux));
        Assert.Equal(StringComparison.Ordinal, PathIdentity.ComparisonFor(OSPlatform.FreeBSD));
    }

    private string Link(string name, string target)
    {
        var path = Path.Combine(_folder, name);
        Directory.CreateSymbolicLink(path, target);
        return path;
    }
}
