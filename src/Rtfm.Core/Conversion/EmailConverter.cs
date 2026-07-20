using System.Text;
using System.Text.RegularExpressions;
using MimeKit;

namespace Rtfm.Core.Conversion;

/// <summary>
/// Exported email chains (<c>.eml</c>, <c>.mbox</c>) — Phase 24. Decisions get
/// made in threads and never make it back into Confluence, and unlike every
/// other input a message carries a real author and a real <c>Date:</c> header.
///
/// Structure over prose: a file becomes one document, each message its own
/// <c>## &lt;date&gt; &lt;sender&gt;</c> section under the subject's <c>#</c>,
/// so the existing heading-aware chunker yields one chunk per message with a
/// <c>subject &gt; date &gt; sender</c> breadcrumb — no chunker change (the
/// Phase 15/18 per-entity granularity lesson, a third time).
///
/// <c>SourceModifiedAt</c> is the newest message in the file. Per-message dates
/// would need a chunk-level contract change; the case that actually drives
/// §2.13 is thread-vs-document recency, and contradiction detection excludes
/// same-document pairs anyway, so the thread's date is the one that matters.
/// </summary>
public sealed partial class EmailConverter
{
    private readonly HtmlToMarkdownConverter _html = new();

    public ConversionResult Convert(Stream input, string sourcePath)
    {
        var messages = Load(input, sourcePath);
        if (messages.Count == 0)
        {
            throw new InvalidDataException($"No email messages found in: {sourcePath}");
        }

        messages.Sort(static (a, b) => a.Date.CompareTo(b.Date));

        var title = ThreadTitle(messages[0], sourcePath);
        var markdown = new StringBuilder();
        markdown.Append("# ").Append(title).Append("\n\n");

        foreach (var message in messages)
        {
            markdown.Append("## ")
                .Append(message.Date.UtcDateTime.ToString("yyyy-MM-dd"))
                .Append(' ')
                .Append(SenderName(message))
                .Append("\n\n");

            var body = CleanBody(message);
            if (body.Length > 0)
            {
                markdown.Append(body).Append("\n\n");
            }
        }

        // The newest message dates the thread.
        var modifiedAt = messages[^1].Date;

        return new ConversionResult(
            SourcePath: sourcePath,
            Format: SourceFormat.Email,
            Markdown: markdown.ToString().TrimEnd() + "\n",
            Title: title,
            SourceModifiedAt: modifiedAt == default ? null : modifiedAt);
    }

    /// <summary>
    /// An <c>.mbox</c> holds a whole folder or thread; an <c>.eml</c> is a
    /// single message. Both parse through MimeKit, already shipped for the
    /// MHTML route.
    /// </summary>
    private static List<MimeMessage> Load(Stream input, string sourcePath)
    {
        var messages = new List<MimeMessage>();

        if (IsMbox(input, sourcePath))
        {
            var parser = new MimeParser(input, MimeFormat.Mbox);
            while (!parser.IsEndOfStream)
            {
                messages.Add(parser.ParseMessage());
            }
        }
        else
        {
            messages.Add(MimeMessage.Load(input));
        }

        return messages;
    }

    private static bool IsMbox(Stream input, string sourcePath)
    {
        if (Path.GetExtension(sourcePath).Equals(".mbox", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!input.CanSeek)
        {
            return false;
        }

        var origin = input.Position;
        var buffer = new byte[5];
        var read = input.Read(buffer, 0, buffer.Length);
        input.Position = origin;

        return read == 5 && Encoding.ASCII.GetString(buffer) == "From ";
    }

    /// <summary>
    /// Plain text is preferred over HTML: it quote-strips far more reliably,
    /// and multipart/alternative almost always carries it. HTML-only messages
    /// route through the shared tail first — ReverseMarkdown renders
    /// <c>blockquote</c> as <c>&gt;</c>, so the same text passes then apply.
    /// </summary>
    private string CleanBody(MimeMessage message)
    {
        var raw = message.TextBody;

        if (string.IsNullOrWhiteSpace(raw))
        {
            var html = message.HtmlBody;
            raw = string.IsNullOrWhiteSpace(html) ? string.Empty : _html.Convert(html).Markdown;
        }

        if (raw.Length == 0)
        {
            return string.Empty;
        }

        var body = EmailSanitizer.StripQuotedHistory(raw);
        body = EmailSanitizer.StripSignature(body);
        return Demote(body);
    }

    /// <summary>
    /// A body line starting with <c>#</c> would be read as a heading by the
    /// chunker and shatter the one-chunk-per-message structure. Escape it —
    /// the character survives into the rendered text either way.
    /// </summary>
    private static string Demote(string body) => BodyHeading().Replace(body, @"\$1");

    private static string SenderName(MimeMessage message)
    {
        var mailbox = message.From.Mailboxes.FirstOrDefault();
        if (mailbox is null)
        {
            return "unknown sender";
        }

        return string.IsNullOrWhiteSpace(mailbox.Name) ? mailbox.Address : mailbox.Name.Trim();
    }

    /// <summary>
    /// The thread's subject, with the reply/forward prefixes stripped so a
    /// chain titles itself the same whichever message opens the file.
    /// </summary>
    private static string ThreadTitle(MimeMessage first, string sourcePath)
    {
        var subject = first.Subject?.Trim();
        if (string.IsNullOrEmpty(subject))
        {
            return Path.GetFileNameWithoutExtension(sourcePath);
        }

        var stripped = ReplyPrefix().Replace(subject, string.Empty).Trim();
        return stripped.Length == 0 ? subject : stripped;
    }

    [GeneratedRegex(@"^(#{1,6}\s)", RegexOptions.Multiline)]
    private static partial Regex BodyHeading();

    [GeneratedRegex(@"^((re|fw|fwd|aw|sv|vs)\s*(\[\d+\])?\s*:\s*)+", RegexOptions.IgnoreCase)]
    private static partial Regex ReplyPrefix();
}
