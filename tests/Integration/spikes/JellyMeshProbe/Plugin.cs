using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace JellyMesh.Probe;

/// <summary>Empty plugin shell. The hooks are discovered by interface.</summary>
public sealed class Plugin : BasePlugin<BasePluginConfiguration>
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    public override string Name => "JellyMesh Probe";

    public override Guid Id => Guid.Parse("6f1c1e0a-7c55-4a0e-9b53-0d5a3b8e2f11");
}
