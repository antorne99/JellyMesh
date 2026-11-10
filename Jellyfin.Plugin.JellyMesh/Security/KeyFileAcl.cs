using System;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Jellyfin.Plugin.JellyMesh.Security;

/// <summary>
/// Builds the explicit access list of the credential key on Windows: the account running Jellyfin, SYSTEM and
/// Administrators, without the permissions of the folder (decision 43).
/// </summary>
internal static class KeyFileAcl
{
    /// <summary>The security identifier of SYSTEM.</summary>
    public const string LocalSystem = "S-1-5-18";

    /// <summary>The security identifier of the built-in Administrators group.</summary>
    public const string Administrators = "S-1-5-32-544";

    /// <summary>
    /// Lists who may use the key when Jellyfin runs as the given account.
    /// </summary>
    /// <param name="processUser">The security identifier of the account running Jellyfin.</param>
    /// <returns>The access list.</returns>
    public static KeyFileAclPlan PlanFor(string processUser)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processUser);
        return new KeyFileAclPlan([.. new[] { processUser, LocalSystem, Administrators }.Distinct(StringComparer.OrdinalIgnoreCase)], true);
    }

    /// <summary>
    /// Builds the access list for the account running this process.
    /// </summary>
    /// <returns>The file security to give the key.</returns>
    /// <exception cref="UnauthorizedAccessException">The process has no user account to grant.</exception>
    [SupportedOSPlatform("windows")]
    public static FileSecurity ForThisProcess()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("The process has no user account");
        return ToFileSecurity(PlanFor(user.Value));
    }

    /// <summary>
    /// Turns an access list into the Windows file security that applies it.
    /// </summary>
    /// <param name="plan">The access list.</param>
    /// <returns>The file security.</returns>
    [SupportedOSPlatform("windows")]
    public static FileSecurity ToFileSecurity(KeyFileAclPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(plan.BlocksInheritance, false);
        foreach (var sid in plan.FullControl)
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }
}
