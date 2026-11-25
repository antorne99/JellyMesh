using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Storage;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Remote;

public class ErrorLogLimitTests
{
    private readonly StepClock _clock = new();

    [Fact]
    public void ErrorsPastTheMinuteAreNotWholeUntilTheNextOne()
    {
        var limit = new ErrorLogLimit(_clock);

        var first = Enumerable.Range(0, 25).Select(_ => limit.Of("whole")).ToList();
        _clock.Now += TimeSpan.FromMinutes(1);

        Assert.Equal(20, first.Count(text => text == "whole"));
        Assert.Equal("whole", limit.Of("whole"));
    }

    [Fact]
    public void AnErrorThatIsNotWrittenAddsNoLine()
    {
        var limit = new ErrorLogLimit(_clock);

        var last = Enumerable.Range(0, 21).Select(_ => limit.Of("whole")).ToList()[^1];

        Assert.Contains("not written whole", last, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', last);
    }

    [Fact]
    public void ADayHoldsAtMost50MillionCharacters()
    {
        var limit = new ErrorLogLimit(_clock);
        var detail = new string('x', 64 * 1024);
        var written = 0L;
        for (var minute = 0; minute < 1439; minute++)
        {
            written += Enumerable.Range(0, 8).Select(_ => limit.Of(detail)).Where(text => text == detail).Sum(text => (long)text.Length);
            _clock.Now += TimeSpan.FromMinutes(1);
        }

        Assert.InRange(written, 50_000_000, 50L * 1024 * 1024);
        Assert.NotEqual(detail, limit.Of(detail));

        _clock.Now += TimeSpan.FromMinutes(1);

        Assert.Equal(detail, limit.Of(detail));
    }

    [Fact]
    public void AServerThatFailsTakesNothingFromTheOthers()
    {
        var limits = new ErrorLogLimits(_clock);
        var failing = Guid.NewGuid();
        for (var call = 0; call < 25; call++)
        {
            limits.For(failing).Of("whole");
            limits.Playback.Of("whole");
        }

        Assert.NotEqual("whole", limits.For(failing).Of("whole"));
        Assert.NotEqual("whole", limits.Playback.Of("whole"));
        Assert.Equal("whole", limits.For(Guid.NewGuid()).Of("whole"));
        Assert.Equal("whole", limits.Unsaved.Of("whole"));
        Assert.Equal("whole", limits.Local.Of("whole"));
    }

    [Fact]
    public async Task AServerWhoseLoginsFailTakesNothingFromTheOthers()
    {
        var fails = false;
        using var fixture = new RegistryFixture(new StubHandler(_ => fails ? StubHandler.Status(HttpStatusCode.InternalServerError) : StubHandler.Json(TestValues.LoginJson)));
        var failing = await fixture.AddServerAsync();
        fails = true;

        for (var attempt = 0; attempt < 25; attempt++)
        {
            await fixture.Registry.CheckEditAsync(failing.Id, new RemoteServerEdit { Name = failing.Name, Url = TestValues.Url, Username = TestValues.Credentials.Username, Password = TestValues.Credentials.Password }, CancellationToken.None);
        }

        Assert.NotEqual("whole", fixture.ErrorLogs.For(failing.Id).Of("whole"));
        Assert.Equal("whole", fixture.ErrorLogs.For(Guid.NewGuid()).Of("whole"));
        Assert.Equal("whole", fixture.ErrorLogs.Unsaved.Of("whole"));
    }

    [Fact]
    public async Task ServersThatCannotBeAddedTakeNothingFromTheSavedOnes()
    {
        using var fixture = new RegistryFixture(new StubHandler(_ => StubHandler.Status(HttpStatusCode.InternalServerError)));

        for (var attempt = 0; attempt < 25; attempt++)
        {
            await fixture.Registry.AddAsync("Friend", TestValues.Url, TestValues.Credentials, CancellationToken.None);
        }

        Assert.NotEqual("whole", fixture.ErrorLogs.Unsaved.Of("whole"));
        Assert.Equal("whole", fixture.ErrorLogs.Local.Of("whole"));
    }

    [Fact]
    public void EveryClassWithTheLimitsGetsThemFromThePlugin()
    {
        var holders = typeof(ErrorLogLimits).Assembly.GetTypes()
            .Where(type => type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Any(field => field.FieldType == typeof(ErrorLogLimits)))
            .ToList();

        Assert.True(holders.Count > 30);
        Assert.All(holders, type => Assert.Contains(
            type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(ErrorLogLimits))));
    }

    // Row 554: the database and the settings store are local log points of 0.6.
    [Fact]
    public void TheDatabaseAndTheSettingsStoreLogTheirErrorsWithinTheLimits()
    {
        Assert.All(
            new[] { typeof(PluginDatabase), typeof(PluginConfigurationStore) },
            type => Assert.Contains(type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic), field => field.FieldType == typeof(ErrorLogLimits)));
    }

    [Fact]
    public void ThePluginHasOneSetOfLimits()
    {
        var services = new ServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, new Mock<IServerApplicationHost>().Object);

        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, service => service.ServiceType == typeof(ErrorLogLimits)).Lifetime);
    }

    private sealed class StepClock : TimeProvider
    {
        public TimeSpan Now { get; set; } = TimeSpan.FromHours(1);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Now.Ticks;
    }
}
