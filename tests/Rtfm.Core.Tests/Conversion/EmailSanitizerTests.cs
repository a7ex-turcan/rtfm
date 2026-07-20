using Rtfm.Core.Conversion;

namespace Rtfm.Core.Tests.Conversion;

/// <summary>
/// The two passes that decide email retrieval quality, tested directly as
/// pure string logic (Core convention: expose the seam, don't test through I/O).
/// </summary>
public class EmailSanitizerTests
{
    [Theory]
    [InlineData("On Fri, 13 Mar 2026 at 17:02, Bob <bob@x.com> wrote:")]
    [InlineData("-----Original Message-----")]
    [InlineData("---------- Forwarded message ---------")]
    [InlineData("________________________________")]
    public void Cuts_the_thread_at_every_quote_boundary_dialect(string boundary)
    {
        var body = $"My reply lives here.\n\n{boundary}\nEarlier text nobody should index.";

        var result = EmailSanitizer.StripQuotedHistory(body);

        Assert.Equal("My reply lives here.", result);
    }

    [Fact]
    public void Cuts_at_an_attribution_line_wrapped_onto_the_next_line()
    {
        var body =
            "Short answer: no.\n\n" +
            "On Fri, 13 Mar 2026 at 17:02, Bob Jones\n" +
            "<bob.jones@a-very-long-domain.example> wrote:\n" +
            "> the quoted question";

        Assert.Equal("Short answer: no.", EmailSanitizer.StripQuotedHistory(body));
    }

    [Fact]
    public void Cuts_at_an_outlook_forwarded_header_block()
    {
        var body =
            "See below.\n\n" +
            "From: Bob Jones\n" +
            "Sent: Friday, 13 March 2026 17:02\n" +
            "To: Alice Smith\n" +
            "Subject: Bundle pricing\n\n" +
            "the forwarded body";

        Assert.Equal("See below.", EmailSanitizer.StripQuotedHistory(body));
    }

    [Fact]
    public void Drops_interleaved_quote_lines_above_the_boundary()
    {
        var body = "Agreed.\n> your point about roles\nBut not the timeline.";

        Assert.Equal("Agreed.\nBut not the timeline.", EmailSanitizer.StripQuotedHistory(body));
    }

    [Fact]
    public void A_from_line_that_is_not_a_forward_header_is_not_a_boundary()
    {
        var body = "From: the design review we learned two things.\nBoth are written up.";

        Assert.Equal(body, EmailSanitizer.StripQuotedHistory(body));
    }

    [Theory]
    [InlineData("--")]
    [InlineData("-- ")]
    public void Cuts_at_the_rfc_3676_delimiter(string delimiter)
    {
        var body = $"The answer is super-admin.\n\n{delimiter}\nAlice Smith\nHead of Platform";

        Assert.Equal("The answer is super-admin.", EmailSanitizer.StripSignature(body));
    }

    [Theory]
    [InlineData("Sent from my iPhone")]
    [InlineData("Get Outlook for Android")]
    [InlineData("This email and any attachments are confidential and intended solely for the addressee.")]
    [InlineData("CONFIDENTIALITY NOTICE: the contents of this transmission are privileged.")]
    [InlineData("If you have received this in error, please notify the sender and delete it.")]
    public void Cuts_at_mobile_footers_and_legal_disclaimers(string trailer)
    {
        var body = $"Ship it on Thursday.\n\n{trailer}";

        Assert.Equal("Ship it on Thursday.", EmailSanitizer.StripSignature(body));
    }

    [Fact]
    public void Drops_a_trailing_contact_block_with_no_delimiter()
    {
        // Outlook omits the "-- " delimiter, so the heuristic carries this case.
        var body =
            "Confirmed, the runbook is updated.\n\n" +
            "Alice Smith\n" +
            "Head of Platform | Corp Ltd\n" +
            "Tel: +44 20 7946 0102\n" +
            "www.corp.example";

        Assert.Equal("Confirmed, the runbook is updated.", EmailSanitizer.StripSignature(body));
    }

    [Fact]
    public void Keeps_a_plain_sign_off()
    {
        var body = "The default role is super-admin.\n\nThanks,\nAlice";

        Assert.Equal(body.Replace("\r\n", "\n"), EmailSanitizer.StripSignature(body));
    }

    [Fact]
    public void Keeps_a_short_body_that_is_not_contact_data()
    {
        var body = "Yes.";

        Assert.Equal("Yes.", EmailSanitizer.StripSignature(body));
    }
}
