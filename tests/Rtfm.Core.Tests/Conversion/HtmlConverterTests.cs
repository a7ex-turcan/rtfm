using System.Text;
using Rtfm.Core.Conversion;

namespace Rtfm.Core.Tests.Conversion;

public class HtmlConverterTests
{
    // A minimal Jira "Export to Word" file: bare HTML (no MIME wrapper) with the
    // application/vnd.ms-word meta tag, the issue title in <title>, an Updated:
    // byline, a heading, and a details table — the shape the real export has.
    private const string SampleHtml =
        "<!DOCTYPE html>\n" +
        "<html>\n<head>\n" +
        "    <title>[#AEXP-130] Standard attribute definition</title>\n" +
        "    <meta http-equiv=\"Content-Type\" Content=\"application/vnd.ms-word; charset=UTF-8\">\n" +
        "    <style type=\"text/css\">.grid{color:red}</style>\n" +
        "</head>\n<body>\n" +
        "<h3 class=\"formtitle\">Standard attribute definition\n" +
        "  <span class=\"subText\">Created: 2026/04/10 &nbsp;Updated: 2026/07/08</span>\n" +
        "</h3>\n" +
        "<table class=\"grid\">\n" +
        "<tr><td><b>Status:</b></td><td>Dev in Progress</td></tr>\n" +
        "<tr><td><b>Priority:</b></td><td>High</td></tr>\n" +
        "</table>\n" +
        "<p>A user attribute must have a stable key &amp; a display name.</p>\n" +
        "</body>\n</html>\n";

    private static ConversionResult Convert()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SampleHtml));
        return new DocumentConverter().Convert(stream, "AEXP-130.doc");
    }

    [Fact]
    public void Routes_bare_html_doc_and_reports_title_from_title_tag()
    {
        var result = Convert();

        Assert.Equal(SourceFormat.Html, result.Format);
        // No <h1>; the shared tail's h1 fallback is empty, so the <title> wins.
        Assert.Equal("[#AEXP-130] Standard attribute definition", result.Title);
    }

    [Fact]
    public void Extracts_updated_byline_as_modified_date()
    {
        var result = Convert();

        Assert.NotNull(result.SourceModifiedAt);
        Assert.Equal(new DateOnly(2026, 7, 8), DateOnly.FromDateTime(result.SourceModifiedAt!.Value.UtcDateTime));
    }

    [Fact]
    public void Converts_body_content_and_tables_to_markdown()
    {
        var markdown = Convert().Markdown;

        Assert.Contains("A user attribute must have a stable key & a display name.", markdown);
        Assert.Contains("| **Status:** | Dev in Progress |", markdown);
        Assert.DoesNotContain("<style", markdown);   // head stripped
        Assert.DoesNotContain("class=", markdown);    // editor cruft stripped
    }
}
