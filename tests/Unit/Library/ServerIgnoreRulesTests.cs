using System;
using System.IO;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public class ServerIgnoreRulesTests
{
    private static readonly string _root = Path.Combine(Path.GetTempPath(), "jellymesh-root");

    private readonly Mock<ILibraryManager> _library = new();

    private ServerIgnoreRules Rules() => new(_library.Object, _root, NullLogger.Instance);

    [Fact]
    public void TheServerIsAskedAboutThePlannedEntryAsAMissingOneWithoutAParent()
    {
        var path = Path.Combine(_root, "a", "Video domestici", "@eaDir");
        _library.Setup(library => library.IgnoreFile(
                It.Is<FileSystemMetadata>(file => file.FullName == path && file.Name == "@eaDir" && file.IsDirectory && !file.Exists),
                It.Is<BaseItem>(parent => parent == null)))
            .Returns(true);

        Assert.True(Rules().Ignores(Path.Combine("a", "Video domestici"), "@eaDir", true));
        Assert.False(Rules().Ignores(Path.Combine("a", "Video domestici"), "@eaDir", false));
    }

    [Fact]
    public void AnEntryInAFolderTheServerIgnoresIsNotIgnoredForItsOwnName()
    {
        _library.Setup(library => library.IgnoreFile(It.IsAny<FileSystemMetadata>(), It.IsAny<BaseItem>())).Returns(true);

        Assert.False(Rules().Ignores("a", "Beach day.strm", false));
    }

    [Fact]
    public void AnErrorOfTheServerCountsAsNotIgnored()
    {
        _library.Setup(library => library.IgnoreFile(It.IsAny<FileSystemMetadata>(), It.IsAny<BaseItem>())).Throws<InvalidOperationException>();

        Assert.False(Rules().Ignores("a", "Beach day.strm", false));
    }
}
