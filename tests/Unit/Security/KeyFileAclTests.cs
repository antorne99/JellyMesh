using Jellyfin.Plugin.JellyMesh.Security;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Security;

public sealed class KeyFileAclTests
{
    private const string JellyfinUser = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private const string LocalSystem = "S-1-5-18";
    private const string Administrators = "S-1-5-32-544";

    [Fact]
    public void OnlyTheJellyfinAccountSystemAndAdministratorsGetTheKey()
    {
        var plan = KeyFileAcl.PlanFor(JellyfinUser);

        Assert.Equal([JellyfinUser, LocalSystem, Administrators], plan.FullControl);
    }

    [Fact]
    public void TheKeyDoesNotInheritThePermissionsOfItsFolder()
    {
        Assert.True(KeyFileAcl.PlanFor(JellyfinUser).BlocksInheritance);
    }

    [Fact]
    public void JellyfinRunningAsSystemIsListedOnce()
    {
        var plan = KeyFileAcl.PlanFor(LocalSystem);

        Assert.Equal([LocalSystem, Administrators], plan.FullControl);
    }
}
