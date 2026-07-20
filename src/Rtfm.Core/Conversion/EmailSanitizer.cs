using System.Text;
using System.Text.RegularExpressions;

namespace Rtfm.Core.Conversion;

/// <summary>
/// Cuts the two kinds of noise an exported message carries: quoted reply
/// history and trailing signature/disclaimer blocks (Phase 24).
///
/// Quote-stripping is not cosmetic. A ten-message chain exported per-message
/// has its first message quoted inside all nine replies, so indexing raw
/// bodies stores it ten times — near-identical text under ten different dates,
/// which is exactly the contradiction detector's nomination signature (§2.13).
/// Phase 22's template suppression does not cover it: that keys on content
/// shared across ≥3 documents, and this duplication is *within* a thread.
///
/// Both passes are deliberately deterministic and dumb. The corpus-frequency
/// alternative (Phase 22's <c>line_hashes</c> would identify signature blocks
/// empirically) needs a second pass over an already-indexed corpus; the plan
/// is to reach for it only if real exports prove messier than these rules.
/// </summary>
internal static partial class EmailSanitizer
{
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
