using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using AngleSharp.Html.Parser;

namespace Rtfm.Core.Conversion;

/// <summary>
/// Converts a draw.io diagram (<c>.drawio</c> / mxfile XML) to markdown
/// (Phase 15). Diagrams are graphs, not pictures: <c>mxCell</c> vertices carry
/// labels (often HTML fragments) and edges carry source→target relations — the
/// knowledge (e.g. DB table relationships) that is otherwise invisible to
/// search. Each page becomes a heading with two sections: <b>Shapes</b>
/// (containers — ER tables, swimlanes — inline their children, so a table
/// renders as name + columns) and <b>Connections</b> (resolved
/// <c>A → B: label</c> lines).
///
/// Pages come in two encodings, both handled: modern saves embed a plain
/// <c>mxGraphModel</c> element; classic saves compress it (base64 → raw
/// DEFLATE → URI-encoding).
/// </summary>
public sealed class DrawioConverter
{
    private static readonly HtmlParser Html = new();

    public ConversionResult Convert(Stream input, string sourcePath)
    {
        var xml = XDocument.Load(input);
        var mxfile = xml.Root ?? throw new InvalidDataException("empty draw.io file");

        var title = Path.GetFileNameWithoutExtension(sourcePath);
        if (title.EndsWith(".drawio", StringComparison.OrdinalIgnoreCase))
        {
            title = title[..^".drawio".Length]; // handle name.drawio.xml
        }

        var sb = new StringBuilder();
        sb.Append("# ").Append(title).Append("\n\n");

        var pageNumber = 0;
        foreach (var diagram in mxfile.Elements("diagram"))
        {
            pageNumber++;
            var pageName = diagram.Attribute("name")?.Value is { Length: > 0 } n ? n : $"Page {pageNumber}";
            var model = LoadGraphModel(diagram);
            if (model is null)
            {
                continue;
            }

            sb.Append("## ").Append(pageName).Append("\n\n");
            RenderPage(sb, model);
        }

        return new ConversionResult(
            SourcePath: sourcePath,
            Format: SourceFormat.Drawio,
            Markdown: sb.ToString().TrimEnd(),
            Title: title,
            SourceModifiedAt: TryParseModified(mxfile));
    }

    /// <summary>Plain child element, or the classic compressed text payload.</summary>
    private static XElement? LoadGraphModel(XElement diagram)
    {
        if (diagram.Element("mxGraphModel") is { } plain)
        {
            return plain;
        }

        var payload = diagram.Value.Trim();
        if (payload.Length == 0)
        {
            return null;
        }

        try
        {
            return XDocument.Parse(DecompressPage(payload)).Root;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or System.Xml.XmlException)
        {
            return null; // unreadable page — skip rather than sink the file
        }
    }

    /// <summary>draw.io's classic page encoding: base64 → raw DEFLATE → URI-encoded XML. Internal for tests.</summary>
    internal static string DecompressPage(string base64)
    {
        var compressed = System.Convert.FromBase64String(base64);
        using var deflate = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var reader = new StreamReader(deflate, Encoding.UTF8);
        return Uri.UnescapeDataString(reader.ReadToEnd());
    }

    private sealed record Cell(string Id, string Label, string Style, string? Parent, bool IsVertex, bool IsEdge, string? Source, string? Target);

    private static void RenderPage(StringBuilder sb, XElement model)
    {
        // Shapes with metadata (links, Mermaid imports, custom attributes) are
        // wrapped: <UserObject id label><mxCell …/></UserObject> (or <object>).
        // The wrapper owns the id and the label; the mxCell inherits both.
        var cells = model.Descendants("mxCell")
            .Select(e =>
            {
                var wrapper = e.Parent is { } p && (p.Name.LocalName is "UserObject" or "object") ? p : null;
                return new Cell(
                    Id: e.Attribute("id")?.Value ?? wrapper?.Attribute("id")?.Value ?? string.Empty,
                    Label: CleanLabel(e.Attribute("value")?.Value ?? wrapper?.Attribute("label")?.Value),
                    Style: e.Attribute("style")?.Value ?? string.Empty,
                    Parent: e.Attribute("parent")?.Value,
                    IsVertex: e.Attribute("vertex")?.Value == "1",
                    IsEdge: e.Attribute("edge")?.Value == "1",
                    Source: e.Attribute("source")?.Value,
                    Target: e.Attribute("target")?.Value);
            })
            .Where(c => c.Id.Length > 0)
            .ToList();

        var byId = cells.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var children = cells.Where(c => c.Parent is not null)
            .GroupBy(c => c.Parent!)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // Shapes: vertices whose parent is a layer (not another vertex) are
        // top-level; containers inline their descendants so an ER table reads
        // as "name — col1; col2; …".
        var shapes = new List<string>();
        foreach (var cell in cells.Where(c => c.IsVertex && (c.Parent is null || !byId.TryGetValue(c.Parent, out var p) || !p.IsVertex)))
        {
            var line = RenderShape(cell, children);
            if (line.Length > 0)
            {
                shapes.Add(line);
            }
        }

        if (shapes.Count > 0)
        {
            sb.Append("### Shapes\n\n");
            foreach (var shape in shapes)
            {
                sb.Append("- ").Append(shape).Append('\n');
            }

            sb.Append('\n');
        }

        // Connections: the relationship knowledge.
        var edges = cells.Where(c => c.IsEdge).ToList();
        if (edges.Count > 0)
        {
            sb.Append("### Connections\n\n");
            foreach (var edge in edges)
            {
                var from = ResolveEndpoint(edge.Source, byId);
                var to = ResolveEndpoint(edge.Target, byId);
                sb.Append("- ").Append(from).Append(" → ").Append(to);
                if (edge.Label.Length > 0)
                {
                    sb.Append(": ").Append(edge.Label);
                }

                sb.Append('\n');
            }

            sb.Append('\n');
        }
    }

    /// <summary>A shape and its inlined descendants: <c>accounts — PK id; owner_id FK; name</c>.</summary>
    private static string RenderShape(Cell cell, Dictionary<string, List<Cell>> children)
    {
        var parts = new List<string>();
        CollectDescendantLabels(cell.Id, children, parts);

        return (cell.Label, parts.Count) switch
        {
            ({ Length: > 0 }, > 0) => $"**{cell.Label}** — {string.Join("; ", parts)}",
            ({ Length: > 0 }, _) => cell.Label,
            (_, > 0) => string.Join("; ", parts),
            _ => string.Empty,
        };
    }

    private static void CollectDescendantLabels(string id, Dictionary<string, List<Cell>> children, List<string> into)
    {
        if (!children.TryGetValue(id, out var kids))
        {
            return;
        }

        foreach (var kid in kids.Where(k => k.IsVertex))
        {
            // A table row's cells (e.g. the "PK" marker + the column name)
            // merge into one entry, so a row reads "PK id" not two items.
            var rowParts = new List<string>();
            if (kid.Label.Length > 0)
            {
                rowParts.Add(kid.Label);
            }

            var nested = new List<string>();
            CollectDescendantLabels(kid.Id, children, nested);
            rowParts.AddRange(nested);

            if (rowParts.Count > 0)
            {
                into.Add(string.Join(" ", rowParts));
            }
        }
    }

    /// <summary>Endpoint label — walking up to the nearest labeled ancestor covers edges attached to table rows.</summary>
    private static string ResolveEndpoint(string? id, Dictionary<string, Cell> byId)
    {
        var hops = 0;
        while (id is not null && byId.TryGetValue(id, out var cell) && hops++ < 5)
        {
            if (cell.Label.Length > 0)
            {
                return cell.Label;
            }

            id = cell.Parent;
        }

        return "?";
    }

    /// <summary>Labels are HTML fragments; strip to text (entities decoded, tags dropped).</summary>
    internal static string CleanLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (!value.Contains('<') && !value.Contains('&'))
        {
            return Collapse(value);
        }

        using var doc = Html.ParseDocument($"<body>{value.Replace("<br>", " ", StringComparison.OrdinalIgnoreCase)}</body>");
        return Collapse(doc.Body?.TextContent ?? string.Empty);
    }

    private static string Collapse(string text)
        => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static DateTimeOffset? TryParseModified(XElement mxfile)
        => DateTimeOffset.TryParse(mxfile.Attribute("modified")?.Value, out var parsed) ? parsed : null;
}
