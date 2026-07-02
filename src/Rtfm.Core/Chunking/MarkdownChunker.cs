using System.Text;
using System.Text.RegularExpressions;

namespace Rtfm.Core.Chunking;

/// <summary>
/// Splits converted markdown into heading-aware chunks (§2.7). Each heading
/// starts a new section; the section's body (everything up to the next heading)
/// becomes one chunk, carrying the full heading breadcrumb. Oversized sections
/// are split on paragraph boundaries with a little overlap so an answer that
/// straddles a split isn't lost. Tables and other blank-line-free blocks are
/// kept intact even if they exceed the size target.
/// </summary>
public sealed class MarkdownChunker
{
    private static readonly Regex HeadingLine = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex ParagraphBreak = new(@"\n{2,}", RegexOptions.Compiled);
    private static readonly Regex TableSeparator = new(@"^\s*\|?[\s:|-]*-[\s:|-]*\|?\s*$", RegexOptions.Compiled);

    private readonly ChunkingOptions _options;

    public MarkdownChunker(ChunkingOptions? options = null) => _options = options ?? ChunkingOptions.Default;

    public IReadOnlyList<Chunk> Chunk(string markdown, ChunkMetadata metadata)
    {
        var segments = SplitIntoSegments(markdown);
        var chunks = new List<Chunk>();
        var stack = new List<(int Level, string Text)>();
        var ordinal = 0;

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];

            string breadcrumb;
            if (segment.Level == 0)
            {
                breadcrumb = metadata.DocumentTitle ?? string.Empty;
            }
            else
            {
                while (stack.Count > 0 && stack[^1].Level >= segment.Level)
                {
                    stack.RemoveAt(stack.Count - 1);
                }
                stack.Add((segment.Level, segment.Heading!));
                breadcrumb = string.Join(" > ", stack.Select(s => s.Text));
            }

            var body = segment.Body.ToString().Trim();

            if (body.Length == 0)
            {
                // A heading with no text of its own that only introduces deeper
                // headings is a pure container — skip it; its label still lives in
                // the breadcrumb of every descendant. Empty preamble is skipped too.
                var isContainer = i + 1 < segments.Count && segments[i + 1].Level > segment.Level;
                if (segment.Level == 0 || isContainer)
                {
                    continue;
                }
            }

            foreach (var window in SplitBody(body))
            {
                chunks.Add(new Chunk(
                    Ordinal: ordinal++,
                    SourcePath: metadata.SourcePath,
                    HeadingPath: breadcrumb,
                    Text: window,
                    DocumentTitle: metadata.DocumentTitle,
                    SourceModifiedAt: metadata.SourceModifiedAt,
                    Project: metadata.Project));
            }
        }

        return chunks;
    }

    private List<Segment> SplitIntoSegments(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var segments = new List<Segment> { new(0, null) };
        var inFence = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }

            Match heading;
            if (!inFence && (heading = HeadingLine.Match(line)).Success)
            {
                segments.Add(new Segment(heading.Groups[1].Value.Length, heading.Groups[2].Value));
            }
            else
            {
                segments[^1].Body.Append(line).Append('\n');
            }
        }

        return segments;
    }

    private IEnumerable<string> SplitBody(string body)
    {
        if (body.Length <= _options.MaxChars)
        {
            yield return body;
            yield break;
        }

        // Blank-line-free blocks bigger than the target (typically a whole
        // markdown table) can't be split on paragraph breaks — break those down
        // first so no single unit blows the size budget.
        var paragraphs = ParagraphBreak.Split(body)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .SelectMany(p => p.Length > _options.MaxChars ? SplitOversizedBlock(p) : [p])
            .ToArray();

        var window = new List<string>();
        var length = 0;

        foreach (var paragraph in paragraphs)
        {
            if (length > 0 && length + paragraph.Length > _options.MaxChars)
            {
                yield return string.Join("\n\n", window);
                window = TailForOverlap(window);
                length = window.Sum(p => p.Length + 2);
            }

            window.Add(paragraph);
            length += paragraph.Length + 2;
        }

        if (window.Count > 0)
        {
            yield return string.Join("\n\n", window);
        }
    }

    /// <summary>
    /// Breaks a single oversized block down. A markdown pipe table is split by
    /// rows with its header + separator repeated in each piece, so every chunk
    /// stays self-describing; anything else is hard-split on line boundaries.
    /// </summary>
    private IEnumerable<string> SplitOversizedBlock(string block)
    {
        var lines = block.Split('\n');

        if (lines.Length >= 2 && lines[0].TrimStart().StartsWith('|') && TableSeparator.IsMatch(lines[1]))
        {
            var header = lines[0] + "\n" + lines[1];
            var current = new StringBuilder(header);

            foreach (var row in lines.Skip(2).Where(l => l.Trim().Length > 0))
            {
                if (current.Length > header.Length && current.Length + 1 + row.Length > _options.MaxChars)
                {
                    yield return current.ToString();
                    current = new StringBuilder(header);
                }

                current.Append('\n').Append(row);
            }

            yield return current.ToString();
            yield break;
        }

        var buffer = new StringBuilder();
        foreach (var line in lines)
        {
            if (buffer.Length > 0 && buffer.Length + 1 + line.Length > _options.MaxChars)
            {
                yield return buffer.ToString();
                buffer.Clear();
            }

            if (buffer.Length > 0)
            {
                buffer.Append('\n');
            }

            buffer.Append(line);
        }

        if (buffer.Length > 0)
        {
            yield return buffer.ToString();
        }
    }

    /// <summary>Trailing paragraphs to repeat at the start of the next window, up to the overlap budget.</summary>
    private List<string> TailForOverlap(List<string> window)
    {
        var carry = new List<string>();
        var length = 0;

        for (var i = window.Count - 1; i >= 0; i--)
        {
            var paragraph = window[i];
            if (paragraph.Length > _options.OverlapChars)
            {
                break; // too big to serve as overlap; would just duplicate a whole block
            }

            if (length + paragraph.Length > _options.OverlapChars && carry.Count > 0)
            {
                break;
            }

            carry.Insert(0, paragraph);
            length += paragraph.Length;
        }

        return carry;
    }

    private sealed class Segment(int level, string? heading)
    {
        public int Level { get; } = level;
        public string? Heading { get; } = heading;
        public StringBuilder Body { get; } = new();
    }
}
