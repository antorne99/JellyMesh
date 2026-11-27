using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests;

/// <summary>The plugin names no network between the servers: a remote URL is whatever the admin enters.</summary>
public class NetworkTests
{
    /// <summary>The variable that holds the names to look for, separated by commas or new lines.</summary>
    public const string NamesVariable = "NETWORK_NAMES";

    private static readonly Assembly _plugin = typeof(FederationAdminController).Assembly;

    /// <summary>Returns the names in the variable, trimmed and without empty entries.</summary>
    public static IReadOnlyList<string> Names() => (Environment.GetEnvironmentVariable(NamesVariable) ?? string.Empty)
        .Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string ReadResource(string name)
    {
        using var stream = _plugin.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Page() => ReadResource(typeof(FederationAdminController).Namespace + ".configPage.html");

    private static IEnumerable<string> UserStrings(Assembly assembly)
    {
        using var file = File.OpenRead(assembly.Location);
        using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader();
        var handle = MetadataTokens.UserStringHandle(1);
        var texts = new List<string>();
        while (!handle.IsNil)
        {
            texts.Add(metadata.GetUserString(handle));
            handle = metadata.GetNextHandle(handle);
        }

        return texts;
    }

    // Hides the names in a text, so that a failure does not print the list.
    private static string Masked(string text, IEnumerable<string> names)
        => names.Aggregate(text, (current, name) => Regex.Replace(current, Regex.Escape(name), "***", RegexOptions.IgnoreCase));

    private static string[] Hits(IEnumerable<(string Where, string Text)> texts)
    {
        var names = Names();
        return texts
            .Where(text => names.Any(name => text.Text.Contains(name, StringComparison.OrdinalIgnoreCase)))
            .Select(text => (text.Where, Masked: Masked(text.Text, names)))
            .Select(text => text.Where + ": " + (text.Masked.Length > 300 ? text.Masked[..300] : text.Masked))
            .ToArray();
    }

    [Fact]
    public void The_server_url_field_has_a_neutral_label_and_placeholder()
    {
        var page = Page();

        Assert.Contains("Url: addInput(grid, t('Server URL'), 'url'),", page, StringComparison.Ordinal);
        Assert.Contains("fields.Url.placeholder = 'http://server:8096';", page, StringComparison.Ordinal);
        Assert.Contains("\"Server URL\": \"URL del server\"", page, StringComparison.Ordinal);
    }

    [NetworkNamesFact]
    public void No_resource_of_the_plugin_names_a_network()
    {
        var resources = _plugin.GetManifestResourceNames()
            .SelectMany(name => ReadResource(name).Split('\n').Select((line, index) => (name + ":" + (index + 1), line)));

        var hits = Hits(resources);

        Assert.True(hits.Length == 0, string.Join(Environment.NewLine, hits));
    }

    [NetworkNamesFact]
    public void No_string_of_the_plugin_or_its_tests_names_a_network()
    {
        var strings = new[] { _plugin, typeof(NetworkTests).Assembly }
            .SelectMany(assembly => UserStrings(assembly).Select(text => (assembly.GetName().Name!, text)));

        var hits = Hits(strings);

        Assert.True(hits.Length == 0, string.Join(Environment.NewLine, hits));
    }
}

/// <summary>A test that runs only when the variable with the network names is set.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class NetworkNamesFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="NetworkNamesFactAttribute"/> class.</summary>
    public NetworkNamesFactAttribute()
    {
        if (NetworkTests.Names().Count == 0)
        {
            Skip = NetworkTests.NamesVariable + " is empty: set it to the network names, separated by commas or new lines, to look for them.";
        }
    }
}
