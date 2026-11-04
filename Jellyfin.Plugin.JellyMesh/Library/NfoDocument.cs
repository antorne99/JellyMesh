using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Writes the <c>.nfo</c> of a stub. Films and episodes are locked with <c>lockdata</c>. A series gets
/// <c>lockedfields</c> instead, because a new item inherits a lock from its parent and would then lose its numbers
/// (decision 1).
/// </summary>
public static class NfoDocument
{
    /// <summary>The fields locked on a series (decision 1).</summary>
    public static readonly IReadOnlyList<MetadataField> SeriesLockedFields =
    [
        MetadataField.Cast,
        MetadataField.Genres,
        MetadataField.ProductionLocations,
        MetadataField.Studios,
        MetadataField.Tags,
        MetadataField.Name,
        MetadataField.Overview,
        MetadataField.OfficialRating
    ];

    /// <summary>
    /// Writes the document of a film.
    /// </summary>
    /// <param name="metadata">The metadata.</param>
    /// <returns>The XML.</returns>
    public static string Movie(StubMetadata metadata) => Write("movie", metadata, static (writer, value) =>
    {
        WriteOptional(writer, "premiered", FormatDate(value.PremiereDate));
        if (value.IsProtected)
        {
            writer.WriteElementString("lockdata", "true");
        }
    });

    /// <summary>
    /// Writes the document of an episode. Without protection it has no numbers: they come from the file name, and the
    /// provider restores them after "replace all metadata".
    /// </summary>
    /// <param name="metadata">The metadata.</param>
    /// <returns>The XML.</returns>
    public static string Episode(StubMetadata metadata) => Write("episodedetails", metadata, static (writer, value) =>
    {
        if (!value.IsProtected)
        {
            return;
        }

        WriteOptional(writer, "season", value.SeasonNumber?.ToString(CultureInfo.InvariantCulture));
        WriteOptional(writer, "episode", value.EpisodeNumber?.ToString(CultureInfo.InvariantCulture));
        WriteOptional(writer, "aired", FormatDate(value.PremiereDate));
        writer.WriteElementString("lockdata", "true");
    });

    /// <summary>
    /// Writes the document of a series.
    /// </summary>
    /// <param name="metadata">The metadata.</param>
    /// <returns>The XML.</returns>
    public static string Series(StubMetadata metadata) => Write("tvshow", metadata, static (writer, value) =>
    {
        if (!value.IsProtected)
        {
            return;
        }

        WriteOptional(writer, "premiered", FormatDate(value.PremiereDate));
        WriteOptional(writer, "enddate", FormatDate(value.EndDate));
        WriteOptional(writer, "status", value.Status);
        writer.WriteElementString("lockedfields", string.Join('|', SeriesLockedFields));
    });

    private static string Write(string root, StubMetadata metadata, Action<XmlWriter, StubMetadata> writeSpecific)
    {
        var buffer = new StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false, Encoding = Encoding.UTF8 };
        using (var writer = XmlWriter.Create(new Utf8StringWriter(buffer), settings))
        {
            writer.WriteStartDocument(true);
            writer.WriteStartElement(root);
            WriteOptional(writer, "title", metadata.Title);
            WriteOptional(writer, "year", metadata.Year?.ToString(CultureInfo.InvariantCulture));
            WriteIds(writer, metadata.ProviderIds);
            if (metadata.IsProtected)
            {
                WriteDetails(writer, metadata);
            }

            writeSpecific(writer, metadata);
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return buffer.ToString();
    }

    private static void WriteIds(XmlWriter writer, IReadOnlyDictionary<string, string> providerIds)
    {
        foreach (var (provider, value) in providerIds.OrderBy(id => id.Key, StringComparer.Ordinal))
        {
            writer.WriteStartElement("uniqueid");
            writer.WriteAttributeString("type", Clean(provider).ToLowerInvariant());
            writer.WriteString(Clean(value));
            writer.WriteEndElement();
        }
    }

    private static void WriteDetails(XmlWriter writer, StubMetadata metadata)
    {
        WriteOptional(writer, "originaltitle", metadata.OriginalTitle);
        WriteOptional(writer, "plot", metadata.Overview);
        WriteOptional(writer, "mpaa", metadata.OfficialRating);
        WriteOptional(writer, "rating", metadata.CommunityRating?.ToString(CultureInfo.InvariantCulture));
        WriteOptional(writer, "criticrating", metadata.CriticRating?.ToString(CultureInfo.InvariantCulture));
        WriteEach(writer, "tagline", metadata.Taglines);
        WriteEach(writer, "genre", metadata.Genres);
        WriteEach(writer, "studio", metadata.Studios);
        WriteEach(writer, "tag", metadata.Tags);

        foreach (var person in metadata.People.Where(person => !string.IsNullOrWhiteSpace(person.Name)))
        {
            writer.WriteStartElement("actor");
            WriteOptional(writer, "name", person.Name);
            WriteOptional(writer, "role", person.Role);
            WriteOptional(writer, "type", person.Type);
            writer.WriteEndElement();
        }
    }

    private static void WriteEach(XmlWriter writer, string element, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            WriteOptional(writer, element, value);
        }
    }

    private static void WriteOptional(XmlWriter writer, string element, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteElementString(element, Clean(value));
        }
    }

    private static string? FormatDate(DateTimeOffset? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // A remote can send characters XML cannot hold, and one of them would make the whole document unreadable.
    private static string Clean(string text)
    {
        var clean = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[index + 1], text[index]))
            {
                clean.Append(text[index]).Append(text[index + 1]);
                index++;
            }
            else if (XmlConvert.IsXmlChar(text[index]))
            {
                clean.Append(text[index]);
            }
        }

        return clean.ToString();
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public Utf8StringWriter(StringBuilder buffer)
            : base(buffer, CultureInfo.InvariantCulture)
        {
        }

        public override Encoding Encoding => Encoding.UTF8;
    }
}
