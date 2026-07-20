using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Rtfm.Core.Conversion;

/// <summary>
/// Finds the boundaries between messages in an exported body, and trims
/// signature/disclaimer blocks (Phase 24).
///
/// **The same boundaries are used two opposite ways, per container** — this is
/// the correctness fix of 1.5.1, and the distinction is load-bearing:
/// <list type="bullet">
/// <item><description><c>.eml</c> is a *single message*, so its quoted history
/// is the only copy of the earlier thread that exists in the file.
/// <see cref="SplitThread"/> cuts it into one segment per message and keeps
/// them all. Dropping it loses the thread — the 1.5.0 bug, where an 11-message
/// chain indexed as only its newest reply.</description></item>
/// <item><description><c>.mbox</c> holds every message *separately*, so the
/// history quoted inside each one is genuinely redundant with its siblings.
/// <see cref="StripQuotedHistory"/> removes it, or a ten-message chain indexes
/// its first message ten times — near-identical text under differing dates,
/// which is the contradiction detector's nomination signature (§2.13), and not
/// something Phase 22's template suppression covers (that keys on content
/// shared across ≥3 *documents*).</description></item>
/// </list>
///
/// The passes are deliberately deterministic and dumb. The corpus-frequency
/// alternative (Phase 22's <c>line_hashes</c> would identify signature blocks
/// empirically) needs a second pass over an already-indexed corpus; the plan
/// is to reach for it only if real exports prove messier than these rules.
/// </summary>
internal static partial class EmailSanitizer
{
    /// <summary>
    /// One message recovered from an exported thread body. <c>Sender</c> and
    /// <c>Date</c> come from the inline header block that introduced it, and
    /// are null for the top message (the caller fills those from the real MIME
    /// headers) or when the block was unparseable.
    /// </summary>
    internal sealed record EmailSegment(string? Sender, DateTimeOffset? Date, string Body);

    /// <summary>
    /// Splits an exported thread body into its constituent messages, newest
    /// first (the order Outlook and Gmail write them), each with whatever
    /// sender/date its inline header block declared.
    ///
    /// Every message appears exactly once in a threaded body — the quoting is
    /// linear, not repetitive — so splitting duplicates nothing.
    /// </summary>
    internal static IReadOnlyList<EmailSegment> SplitThread(string body)
    {
        var lines = SplitLines(body);
        var segments = new List<EmailSegment>();

        string? pendingSender = null;
        DateTimeOffset? pendingDate = null;
        var current = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var header = TryReadHeaderBlock(lines, i);
            if (header is null)
            {
                current.Add(lines[i]);
                continue;
            }

            segments.Add(new EmailSegment(pendingSender, pendingDate, Finish(current)));
            current.Clear();

            pendingSender = header.Sender;
            pendingDate = header.Date;
            i = header.EndIndex;
        }

        segments.Add(new EmailSegment(pendingSender, pendingDate, Finish(current)));

        return segments.Where(static s => s.Body.Length > 0 || s.Sender is not null).ToList();

        static string Finish(List<string> lines)
        {
            var copy = new List<string>(lines);

            // A divider ("____…" / "-----Original Message-----") belongs to the
            // boundary that follows, not to the message it trails.
            while (copy.Count > 0
                   && (string.IsNullOrWhiteSpace(copy[^1])
                       || OriginalMessageDivider().IsMatch(copy[^1].Trim())))
            {
                copy.RemoveAt(copy.Count - 1);
            }

            var text = Rejoin(copy);
            text = InlineImageRef().Replace(text, string.Empty);
            return StripSignature(text);
        }
    }

    private sealed record HeaderBlock(string? Sender, DateTimeOffset? Date, int EndIndex);

    /// <summary>
    /// Recognizes the inline header block that introduces a quoted message and
    /// returns the index of its last line. Two dialects appear in real exports,
    /// sometimes in the same file: <c>From:</c> + <c>Sent:</c> (Outlook) and
    /// <c>From:</c> + <c>Date:</c>. Gmail's <c>On … wrote:</c> is handled too.
    /// </summary>
    private static HeaderBlock? TryReadHeaderBlock(string[] lines, int index)
    {
        var line = lines[index].TrimEnd();

        if (AttributionOpener().IsMatch(line.Trim()))
        {
            var (sender, date) = ParseAttribution(line);
            return new HeaderBlock(sender, date, index);
        }

        if (index + 1 < lines.Length
            && line.TrimStart().StartsWith("On ", StringComparison.OrdinalIgnoreCase)
            && AttributionOpener().IsMatch($"{line.Trim()} {lines[index + 1].Trim()}"))
        {
            var (sender, date) = ParseAttribution($"{line.Trim()} {lines[index + 1].Trim()}");
            return new HeaderBlock(sender, date, index + 1);
        }

        if (!line.TrimStart().StartsWith("From:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // A bare "From:" is only a boundary when a Sent:/Date: follows close by —
        // otherwise it is prose ("From: the design review we learned...").
        var hasTimestamp = false;
        var end = index;
        DateTimeOffset? date2 = null;

        for (var i = index + 1; i < Math.Min(index + 8, lines.Length); i++)
        {
            var candidate = lines[i].TrimStart();
            if (candidate.Length == 0)
            {
                break;
            }

            if (!MessageHeaderLine().IsMatch(candidate))
            {
                break;
            }

            if (SentOrDateHeader().IsMatch(candidate))
            {
                hasTimestamp = true;
                date2 = ParseHeaderDate(candidate[(candidate.IndexOf(':') + 1)..]);
            }

            end = i;
        }

        return hasTimestamp
            ? new HeaderBlock(ParseSender(line[(line.IndexOf(':') + 1)..]), date2, end)
            : null;
    }

    private static (string? Sender, DateTimeOffset? Date) ParseAttribution(string line)
    {
        // "On Fri, 13 Mar 2026 at 17:02, Bob Jones <bob@x> wrote:"
        var inner = line.Trim();
        inner = inner[2..^"wrote:".Length].Trim(' ', ',', ':');

        var lastComma = inner.LastIndexOf(',');
        if (lastComma < 0)
        {
            return (null, null);
        }

        return (ParseSender(inner[(lastComma + 1)..]), ParseHeaderDate(inner[..lastComma]));
    }

    /// <summary>"Alice Smith &lt;alice@corp&gt;" → "Alice Smith"; bare address → the address.</summary>
    private static string? ParseSender(string value)
    {
        var text = value.Trim();
        var angle = text.IndexOf('<');
        if (angle >= 0)
        {
            var name = text[..angle].Trim().Trim('"');
            if (name.Length > 0)
            {
                return name;
            }

            var close = text.IndexOf('>', angle);
            return close > angle ? text[(angle + 1)..close].Trim() : null;
        }

        return text.Length > 0 ? text : null;
    }

    /// <summary>
    /// Outlook writes "Friday, July 17, 2026 7:46 PM" and, in the other
    /// dialect, "Friday, July 17, 2026 at 03:24" — the " at " defeats
    /// <c>TryParse</c>, so it is normalized away first.
    /// </summary>
    private static DateTimeOffset? ParseHeaderDate(string value)
    {
        var text = value.Trim().Replace(" at ", " ", StringComparison.OrdinalIgnoreCase);
        if (text.Length == 0)
        {
            return null;
        }

        foreach (var culture in new[] { CultureInfo.InvariantCulture, UsCulture })
        {
            if (DateTimeOffset.TryParse(text, culture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static readonly CultureInfo UsCulture = CultureInfo.GetCultureInfo("en-US");

    /// <summary>
    /// Everything from the first quote marker to the end of the body is reply
    /// history. Cutting to the end rather than deleting marked lines also
    /// disposes of the quoted senders' signatures, so <see cref="StripSignature"/>
    /// only ever sees the author's own trailer.
    /// </summary>
    internal static string StripQuotedHistory(string body)
    {
        var lines = SplitLines(body);
        var cut = lines.Length;

        for (var i = 0; i < lines.Length; i++)
        {
            if (IsQuoteBoundary(lines, i))
            {
                cut = i;
                break;
            }
        }

        var kept = new List<string>(cut);
        for (var i = 0; i < cut; i++)
        {
            // A stray '>' line above the boundary — inline quoting, or a
            // client that interleaves. Drop the line, keep the reply around it.
            if (!QuotedLine().IsMatch(lines[i]))
            {
                kept.Add(lines[i]);
            }
        }

        return Rejoin(kept);
    }

    /// <summary>
    /// Trims the trailing signature, legal disclaimer, or mobile footer.
    /// </summary>
    internal static string StripSignature(string body)
    {
        var lines = SplitLines(body);
        var cut = lines.Length;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();

            // RFC 3676 delimiter. Reliable when present — but Outlook omits
            // it, which is why it can't be the only rule.
            if (line is "--" or "-- ")
            {
                cut = i;
                break;
            }

            var trimmed = line.TrimStart();

            if (MobileFooter().IsMatch(trimmed) || DisclaimerOpener().IsMatch(trimmed))
            {
                cut = i;
                break;
            }
        }

        var kept = new List<string>(lines.Take(cut));
        TrimContactBlock(kept);
        return Rejoin(kept);
    }

    /// <summary>
    /// Drops a trailing run of short lines that read as contact details —
    /// phone numbers, URLs, addresses, job titles. Bounded to the last
    /// <c>MaxBlock</c> lines so it can never eat a body paragraph, and it only
    /// fires when the majority of the run looks like contact data.
    /// </summary>
    private static void TrimContactBlock(List<string> lines)
    {
        const int MaxBlock = 8;
        const int MaxLineLength = 60;

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var start = lines.Count;
        while (start > 0
               && lines.Count - start < MaxBlock
               && !string.IsNullOrWhiteSpace(lines[start - 1])
               && lines[start - 1].Trim().Length <= MaxLineLength)
        {
            start--;
        }

        var block = lines.Count - start;
        if (block < 2)
        {
            return;
        }

        var contactish = 0;
        for (var i = start; i < lines.Count; i++)
        {
            if (ContactLine().IsMatch(lines[i]))
            {
                contactish++;
            }
        }

        // Majority rule: a two-line "Thanks,\nAlex" sign-off scores 0 and stays.
        if (contactish * 2 > block)
        {
            lines.RemoveRange(start, block);
        }
    }

    private static bool IsQuoteBoundary(string[] lines, int index)
    {
        var line = lines[index].Trim();

        if (line.Length == 0)
        {
            return false;
        }

        if (OriginalMessageDivider().IsMatch(line))
        {
            return true;
        }

        // "On <date>, <someone> wrote:" — clients wrap it across two lines when
        // the address is long, so test the pair joined as well as the line
        // alone. Joining keeps "wrote:" mandatory: matching a bare leading
        // "On " would cut real prose ("On Tuesday we shipped...").
        if (AttributionOpener().IsMatch(line))
        {
            return true;
        }

        if (line.StartsWith("On ", StringComparison.OrdinalIgnoreCase)
            && index + 1 < lines.Length
            && AttributionOpener().IsMatch($"{line} {lines[index + 1].Trim()}"))
        {
            return true;
        }

        // Outlook's header block: a From: line followed shortly by Sent/To/Subject.
        if (line.StartsWith("From:", StringComparison.OrdinalIgnoreCase))
        {
            for (var i = index + 1; i < Math.Min(index + 5, lines.Length); i++)
            {
                if (ForwardedHeader().IsMatch(lines[i].TrimStart()))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string[] SplitLines(string body) =>
        body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string Rejoin(List<string> lines)
    {
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var text = new StringBuilder();
        foreach (var line in lines)
        {
            text.Append(line).Append('\n');
        }

        return text.ToString().Trim();
    }

    [GeneratedRegex(@"^\s*>")]
    private static partial Regex QuotedLine();

    [GeneratedRegex(@"^\s*(-{2,}\s*Original Message\s*-{2,}|_{10,}|-{10,}\s*Forwarded message\s*-{0,})\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex OriginalMessageDivider();

    [GeneratedRegex(@"^On\b.{0,200}\bwrote\s*:?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex AttributionOpener();

    [GeneratedRegex(@"^(Sent|To|Subject|Date|Cc):", RegexOptions.IgnoreCase)]
    private static partial Regex ForwardedHeader();

    [GeneratedRegex(@"^(From|Sent|Date|To|Cc|Bcc|Subject|Reply-To|Importance|Attachments):",
        RegexOptions.IgnoreCase)]
    private static partial Regex MessageHeaderLine();

    [GeneratedRegex(@"^(Sent|Date):", RegexOptions.IgnoreCase)]
    private static partial Regex SentOrDateHeader();

    [GeneratedRegex(@"\[cid:[^\]]+\]")]
    private static partial Regex InlineImageRef();

    [GeneratedRegex(@"^(Sent|Get) (from|Outlook)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MobileFooter();

    [GeneratedRegex(
        @"^(This (e-?mail|message)|The information (in|contained)|CONFIDENTIAL|DISCLAIMER|Privileged/?Confidential|If you (have received|are not the intended))",
        RegexOptions.IgnoreCase)]
    private static partial Regex DisclaimerOpener();

    [GeneratedRegex(
        @"(^\s*(tel|phone|mob|mobile|fax|direct|office|e|email|web|www)\b\s*[:.]?|\+?\d[\d\s().-]{7,}|https?://|www\.|\S+@\S+\.\S+|\b(Ltd|LLC|GmbH|Inc\.?|Limited)\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ContactLine();
}
