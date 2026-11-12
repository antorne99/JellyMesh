using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Provider;
using Jellyfin.Plugin.JellyMesh.Sync;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Configuration;

public class PluginConfigurationTests
{
    [Fact]
    public void DefaultsFollowThePlan()
    {
        var config = new PluginConfiguration();

        Assert.Equal(900, config.SyncIntervalSeconds);
        Assert.Equal("03:00", config.ReconciliationTime);
        Assert.True(config.MetadataProtection);
        Assert.Empty(config.FederatedFolderPath);
        Assert.Empty(config.Servers);
        config.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1)]
    [InlineData(299)]
    public void IntervalBelowTheMinimumIsRefused(int seconds)
    {
        Assert.Throws<ArgumentException>(new PluginConfiguration { SyncIntervalSeconds = seconds }.Validate);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(301)]
    [InlineData(86400)]
    public void IntervalFromTheMinimumUpIsAccepted(int seconds)
    {
        new PluginConfiguration { SyncIntervalSeconds = seconds }.Validate();
    }

    [Fact]
    public void TheMinimumIntervalIsTheOverlapOfTheIncrementalSync()
    {
        Assert.Equal(CatalogSyncService.ChangeOverlap, TimeSpan.FromSeconds(PluginConfiguration.MinSyncIntervalSeconds));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(60)]
    public void NormalizeRaisesASavedIntervalToTheMinimum(int seconds)
    {
        var config = new PluginConfiguration { SyncIntervalSeconds = seconds };

        var corrected = Assert.Single(config.Normalize());

        Assert.Equal(300, config.SyncIntervalSeconds);
        Assert.Equal(new CorrectedSetting(nameof(PluginConfiguration.SyncIntervalSeconds), seconds, 300), corrected);
    }

    [Fact]
    public void NormalizeLeavesAValidConfigurationAlone()
    {
        var config = new PluginConfiguration();

        Assert.Empty(config.Normalize());
        Assert.Equal(900, config.SyncIntervalSeconds);
    }

    [Fact]
    public void ThePlaybackTimesDefaultToDecision17()
    {
        var times = PlaybackTimes.From(new PluginConfiguration());

        Assert.Equal(TimeSpan.FromSeconds(10), times.RemotePlaybackInfo);
        Assert.Equal(TimeSpan.FromSeconds(15), times.StreamOpen);
        Assert.Equal(TimeSpan.FromSeconds(90), times.Idle);
        Assert.Equal(TimeSpan.FromHours(24), times.SessionRetention);
        Assert.Equal(TimeSpan.FromMinutes(5), times.PolicyRetention);
    }

    public static TheoryData<Action<PluginConfiguration, int>, int, int> PlaybackRanges => new()
    {
        { (config, value) => config.RemotePlaybackInfoTimeoutSeconds = value, 3, 60 },
        { (config, value) => config.StreamOpenTimeoutSeconds = value, 5, 60 },
        { (config, value) => config.PlaybackIdleSeconds = value, 30, 109 },
        { (config, value) => config.SessionRetentionHours = value, 1, 168 },
        { (config, value) => config.PolicyCacheMinutes = value, 1, 60 }
    };

    [Theory]
    [MemberData(nameof(PlaybackRanges))]
    public void APlaybackTimeMustBeInItsRange(Action<PluginConfiguration, int> set, int min, int max)
    {
        foreach (var valid in new[] { min, max })
        {
            var config = new PluginConfiguration();
            set(config, valid);
            config.Validate();
        }

        foreach (var invalid in new[] { min - 1, max + 1, 0, -1 })
        {
            var config = new PluginConfiguration();
            set(config, invalid);
            Assert.Throws<ArgumentException>(config.Validate);
        }
    }

    [Theory]
    [MemberData(nameof(PlaybackRanges))]
    public void NormalizeBringsAPlaybackTimeBackIntoItsRange(Action<PluginConfiguration, int> set, int min, int max)
    {
        var low = new PluginConfiguration();
        set(low, min - 1);
        var high = new PluginConfiguration();
        set(high, max + 1);

        Assert.Equal(min, Assert.Single(low.Normalize()).Applied);
        Assert.Equal(max, Assert.Single(high.Normalize()).Applied);
        low.Validate();
        high.Validate();
    }

    [Fact]
    public void TheHighestIdleTimeEndsBeforeASlotOfAProviderExpires()
    {
        var latest = TimeSpan.FromSeconds(PluginConfiguration.MaxPlaybackIdleSeconds) + PlaybackTimeouts.SweepInterval;

        Assert.True(latest < StreamSlots.IdleTimeout);
        Assert.Equal(109, PluginConfiguration.MaxPlaybackIdleSeconds);
    }

    [Fact]
    public void TheDefaultIdleTimeIsWithinTheLimit()
    {
        Assert.True(PlaybackTimeouts.Idle.TotalSeconds <= PluginConfiguration.MaxPlaybackIdleSeconds);
    }

    [Fact]
    public void HoldingLibrariesAgainKeepsASkippedCheckOfTheEntry()
    {
        var config = new PluginConfiguration();
        var serverId = Guid.NewGuid();
        config.HoldAutomaticLibraries(serverId, [new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Casa - Film", ItemId = "a1" }], [], [], true);

        config.HoldAutomaticLibraries(serverId, [new AutomaticLibraryConfig { Kind = ServerLibraryKind.Series, Name = "Casa - Serie", ItemId = "b2" }], [], [], false);

        Assert.True(Assert.Single(config.RemovedServerLibraries).FollowSkipped);
    }

    [Fact]
    public void TakingBackHeldLibrariesKeepsAnEntryOnlyWhileItListsReleasedOnes()
    {
        var config = new PluginConfiguration();
        var kept = Guid.NewGuid();
        var dropped = Guid.NewGuid();
        var library = new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Casa - Film", ItemId = "a1" };
        config.HoldAutomaticLibraries(kept, [library], [], [], false);
        config.RemovedServerLibraries[0].ReleasedLibraries.Add("Casa - Serie");
        config.HoldAutomaticLibraries(dropped, [library], [], [], false);

        config.ReleaseHeldAutomaticLibraries(kept, [library]);
        config.ReleaseHeldAutomaticLibraries(dropped, [library]);

        var held = Assert.Single(config.RemovedServerLibraries);
        Assert.Equal(kept, held.ServerId);
        Assert.Empty(held.Libraries);
        Assert.Equal(["Casa - Serie"], held.ReleasedLibraries);
    }

    [Fact]
    public void TheServerRemovalDialogSaysItsAutomaticLibrariesAreDeletedToo()
    {
        var page = ConfigPage();
        var start = page.IndexOf("button(t('Remove'), function () {", StringComparison.Ordinal);
        var end = page.IndexOf("act(api('DELETE', 'Servers/'", start, StringComparison.Ordinal);
        var dialog = page[start..end];

        Assert.Contains("ownState(server)", dialog, StringComparison.Ordinal);
        Assert.Contains("t('Its automatic libraries are deleted too: {names}.', { names: escapeHtml(", dialog, StringComparison.Ordinal);
        Assert.Contains("t('Its automatic libraries are deleted too.')", dialog, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSyncHistoryShowsTheOutcomeRightAfterTheStart()
    {
        var page = ConfigPage();
        var row = Slice(page, "function historyRow(run)", "function renderHistory(view)");
        var table = Slice(page, "function renderHistory(view)", "container.appendChild(table)");

        Assert.Contains("[t('Started'), t('Outcome'), t('Type'), t('Duration'), t('Servers')]", table, StringComparison.Ordinal);
        var cells = new[] { "row.appendChild(started)", "row.appendChild(outcome)", "runKinds[run.Kind]", "formatDuration(run.DurationMilliseconds)", "row.appendChild(servers)" }
            .Select(cell => row.IndexOf(cell, StringComparison.Ordinal))
            .ToList();
        Assert.DoesNotContain(-1, cells);
        Assert.Equal(cells.Order(), cells);
    }

    // Decision 191: the totals also count the stubs of the single folder and of removed servers.
    [Fact]
    public void TheSyncHistoryShowsTheTotalsOfEachRun()
    {
        var history = Slice(ConfigPage(), "function historyServerLine(server)", "function renderHistory(view)");

        Assert.Contains("t('In total: {created} created, {updated} updated, {removed} removed.'", history, StringComparison.Ordinal);
        foreach (var total in new[] { "run.Created", "run.Updated", "run.Removed", "run.Failed" })
        {
            Assert.Contains(total, history, StringComparison.Ordinal);
        }
    }

    // Decision 211: the intro of the history names the totals line under the server lines.
    [Fact]
    public void TheSyncHistoryIntroMentionsTheTotals()
    {
        var page = ConfigPage();
        var intro = Slice(page, "<h3 class=\"sectionTitle\">Sync history</h3>", "id=\"jmHistoryWarning\"");

        Assert.Contains("removed in its folder, then the totals of the whole folder.</p>", intro, StringComparison.Ordinal);
        Assert.Contains("rimossi nella sua cartella, poi i totali di tutta la cartella.\"", page, StringComparison.Ordinal);
    }

    private static string Slice(string page, string start, string end)
    {
        var from = page.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, start);
        var to = page.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to >= 0, end);
        return page[from..to];
    }

    private static string ConfigPage()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.JellyMesh.Configuration.configPage.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Theory]
    [InlineData("jmSyncInterval", 300, null)]
    [InlineData("jmRemotePlaybackInfoTimeout", 3, 60)]
    [InlineData("jmStreamOpenTimeout", 5, 60)]
    [InlineData("jmPlaybackIdle", 30, 109)]
    [InlineData("jmSessionRetention", 1, 168)]
    [InlineData("jmPolicyCache", 1, 60)]
    public void TheConfigPageOffersTheSameRanges(string id, int min, int? max)
    {
        var input = Regex.Match(ConfigPage(), "<input id=\"" + id + "\"[^>]*>").Value;

        Assert.Contains("min=\"" + min + "\"", input, StringComparison.Ordinal);
        if (max is not null)
        {
            Assert.Contains("max=\"" + max + "\"", input, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("RemotePlaybackInfoTimeoutSeconds")]
    [InlineData("StreamOpenTimeoutSeconds")]
    [InlineData("PlaybackIdleSeconds")]
    [InlineData("SessionRetentionHours")]
    [InlineData("PolicyCacheMinutes")]
    public void TheConfigPageLoadsAndSavesEveryPlaybackTime(string setting)
    {
        var page = ConfigPage();

        Assert.Equal(2, Regex.Matches(page, "config\\." + setting + "\\b").Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("3:00")]
    [InlineData("24:00")]
    [InlineData("03:60")]
    [InlineData("03:00:00")]
    [InlineData("3am")]
    public void TimeMustBeHoursAndMinutes(string time)
    {
        Assert.Throws<ArgumentException>(new PluginConfiguration { ReconciliationTime = time }.Validate);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("/federata/../etc")]
    public void FolderMustBeAUsableAbsolutePath(string path)
    {
        Assert.Throws<ArgumentException>(new PluginConfiguration { FederatedFolderPath = path }.Validate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/federata")]
    [InlineData("/mnt/data/federata/")]
    public void FolderCanBeEmptyOrAbsolute(string path)
    {
        new PluginConfiguration { FederatedFolderPath = path }.Validate();
    }

    [Fact]
    public void AdoptingFromItselfKeepsTheServers()
    {
        var config = new PluginConfiguration();
        config.Servers.Add(new RemoteServerConfig());

        config.AdoptServersFrom(config);

        Assert.Single(config.Servers);
    }
}
