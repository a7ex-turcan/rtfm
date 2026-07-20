using System.Text;
using Rtfm.Core.Chunking;
using Rtfm.Core.Conversion;

namespace Rtfm.Core.Tests.Conversion;

public class EmailConverterTests
{
    private const string SampleEml =
        "From: Alice Smith <alice@corp.example>\r\n" +
        "To: Bob Jones <bob@corp.example>\r\n" +
        "Subject: Re: Bundle pricing\r\n" +
        "Date: Sat, 14 Mar 2026 09:30:00 +0000\r\n" +
        "Message-ID: <2@corp.example>\r\n" +
        "MIME-Version: 1.0\r\n" +
        "Content-Type: text/plain; charset=utf-8\r\n" +
        "\r\n" +
        "We changed the default role to super-admin last week.\r\n" +
        "\r\n" +
        "Thanks,\r\n" +
        "Alice\r\n" +
        "\r\n" +
        "--\r\n" +
        "Alice Smith | Head of Platform\r\n" +
        "Tel: +44 20 7946 0102\r\n" +
        "https://corp.example\r\n" +
        "\r\n" +
        "On Fri, 13 Mar 2026 at 17:02, Bob Jones <bob@corp.example> wrote:\r\n" +
        "> Is the default role still admin?\r\n" +
        "> Please confirm before we ship.\r\n";

    private const string SampleMbox =
        "From bob@corp.example Fri Mar 13 17:02:00 2026\r\n" +
        "From: Bob Jones <bob@corp.example>\r\n" +
        "To: Alice Smith <alice@corp.example>\r\n" +
        "Subject: Bundle pricing\r\n" +
        "Date: Fri, 13 Mar 2026 17:02:00 +0000\r\n" +
        "\r\n" +
        "Is the default role still admin?\r\n" +
        "\r\n" +
        "From alice@corp.example Sat Mar 14 09:30:00 2026\r\n" +
        "From: Alice Smith <alice@corp.example>\r\n" +
        "To: Bob Jones <bob@corp.example>\r\n" +
        "Subject: Re: Bundle pricing\r\n" +
        "Date: Sat, 14 Mar 2026 09:30:00 +0000\r\n" +
        "\r\n" +
        "No, it is super-admin now.\r\n" +
        "\r\n" +
        "On Fri, 13 Mar 2026, Bob Jones <bob@corp.example> wrote:\r\n" +
        "> Is the default role still admin?\r\n" +
        "\r\n" +
        "From bob@corp.example Sun Mar 15 08:00:00 2026\r\n" +
        "From: Bob Jones <bob@corp.example>\r\n" +
        "To: Alice Smith <alice@corp.example>\r\n" +
        "Subject: Re: Bundle pricing\r\n" +
        "Date: Sun, 15 Mar 2026 08:00:00 +0000\r\n" +
        "\r\n" +
        "Understood, updating the runbook.\r\n";

    private static ConversionResult Convert(string sample, string path)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(sample));
        return new DocumentConverter().Convert(stream, path);
    }

    [Fact]
    public void Detects_email_by_conversational_headers_not_the_mime_wrapper()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(SampleEml));

        Assert.Equal(SourceFormat.Email, FormatDetector.Detect("thread.eml", stream));
        Assert.Equal(0, stream.Position);
    }

    /// <summary>
    /// The Phase 24 trap, pinned from both sides: MHTML is itself a MIME email
    /// container, so a Confluence export must keep routing to MHTML while a
    /// real message routes to Email. If either drifts, the other breaks.
    /// </summary>
    [Fact]
    public void Confluence_mhtml_still_routes_to_mhtml()
    {
        const string ConfluenceExport =
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/related; boundary=\"BOUNDARY\"\r\n" +
            "Subject: Access Control Model\r\n" +
            "Date: Tue, 10 Feb 2026 11:00:00 +0000\r\n" +
            "\r\n" +
            "--BOUNDARY\r\n" +
            "Content-Location: file:///C:/page.htm\r\n" +
            "Content-Type: text/html; charset=UTF-8\r\n" +
            "\r\n" +
            "<html><body><h1>Access Control Model</h1></body></html>\r\n" +
            "--BOUNDARY--\r\n";

        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(ConfluenceExport));

        Assert.Equal(SourceFormat.Mhtml, FormatDetector.Detect("page.doc", stream));
    }

    [Fact]
    public void Renders_each_message_as_its_own_section_under_the_subject()
    {
        var result = Convert(SampleEml, "thread.eml");

        Assert.Equal(SourceFormat.Email, result.Format);
        Assert.Equal("Bundle pricing", result.Title);          // "Re: " stripped
        Assert.Contains("# Bundle pricing", result.Markdown);
        Assert.Contains("## 2026-03-14 Alice Smith", result.Markdown);
        Assert.Contains("super-admin", result.Markdown);
    }

    [Fact]
    public void Drops_quoted_history_signature_and_contact_block()
    {
        var markdown = Convert(SampleEml, "thread.eml").Markdown;

        Assert.DoesNotContain("Is the default role still admin?", markdown);
        Assert.DoesNotContain("wrote:", markdown);
        Assert.DoesNotContain("Head of Platform", markdown);
        Assert.DoesNotContain("+44 20 7946", markdown);
        Assert.DoesNotContain("corp.example", markdown);

        // A plain sign-off is not a signature block and must survive.
        Assert.Contains("Thanks,", markdown);
    }

    [Fact]
    public void Dates_the_thread_by_its_newest_message()
    {
        var result = Convert(SampleMbox, "thread.mbox");

        Assert.Equal(
            new DateTimeOffset(2026, 3, 15, 8, 0, 0, TimeSpan.Zero),
            result.SourceModifiedAt);
    }

    [Fact]
    public void Mbox_yields_one_chunk_per_message_with_a_subject_date_sender_breadcrumb()
    {
        var result = Convert(SampleMbox, "thread.mbox");
        var metadata = new ChunkMetadata("thread.mbox", result.Title, result.SourceModifiedAt);

        var chunks = new MarkdownChunker().Chunk(result.Markdown, metadata);

        // Three messages in, three chunks out — the quoted copy of message one
        // inside message two must not become a fourth.
        Assert.Equal(3, chunks.Count);
        Assert.Equal("Bundle pricing > 2026-03-13 Bob Jones", chunks[0].HeadingPath);
        Assert.Equal("Bundle pricing > 2026-03-14 Alice Smith", chunks[1].HeadingPath);
        Assert.Equal("Bundle pricing > 2026-03-15 Bob Jones", chunks[2].HeadingPath);

        Assert.Contains("Is the default role still admin?", chunks[0].Text);
        Assert.DoesNotContain("Is the default role still admin?", chunks[1].Text);
    }

    [Fact]
    public void Body_headings_cannot_shatter_the_per_message_structure()
    {
        var eml =
            "From: Alice <alice@corp.example>\r\n" +
            "To: Bob <bob@corp.example>\r\n" +
            "Subject: Ticket triage\r\n" +
            "Date: Sat, 14 Mar 2026 09:30:00 +0000\r\n" +
            "\r\n" +
            "# 4172 is the blocker, ## 4180 can wait.\r\n";

        var result = Convert(eml, "triage.eml");
        var chunks = new MarkdownChunker().Chunk(
            result.Markdown,
            new ChunkMetadata("triage.eml", result.Title, result.SourceModifiedAt));

        Assert.Single(chunks);
        Assert.Equal("Ticket triage > 2026-03-14 Alice", chunks[0].HeadingPath);
    }
}
