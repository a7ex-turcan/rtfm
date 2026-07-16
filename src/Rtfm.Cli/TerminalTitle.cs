namespace Rtfm.Cli;

/// <summary>
/// Sets the terminal tab/window title while a long-running command (currently
/// <c>watch</c>) is active, with a small animated icon for a bonus sign-of-life.
/// Emitted as an OSC&nbsp;0 escape (<c>ESC ] 0 ; text BEL</c>) on stderr — the
/// same stream the watch dashboard uses — and only when stderr is an interactive
/// terminal (<see cref="Ui.Fancy"/>), so redirected/piped output is never polluted
/// (the §Stream-contract rule). Restores the prior title on <see cref="Dispose"/>.
///
/// Callers MUST serialize their <see cref="Animate"/> / <see cref="Status"/> calls
/// against any other stderr writer (the dashboard writes under <c>lock(state)</c>);
/// two writers to the same stream can otherwise interleave bytes.
/// </summary>
internal sealed class TerminalTitle : IDisposable
{
    // OSC 0 = set icon-name + window-title. BEL (U+0007) is the terminator with
    // the widest support (Windows Terminal, iTerm2, gnome-terminal, tmux); it is
    // part of the OSC sequence, not a standalone bell — it does not ring. Built
    // from char codes so the source carries no invisible control bytes.
    private const char Escape = (char)27;
    private const char Bel = (char)7;
    private static readonly string OscPrefix = Escape + "]0;";

    // Moon-phase spinner: a clean 8-frame rotation that renders as an actual icon
    // in modern terminals. On a terminal Spectre reports as non-unicode we fall
    // back to an ASCII spinner so the title text still animates without mojibake.
    private static readonly string[] UnicodeFrames = ["🌑", "🌒", "🌓", "🌔", "🌕", "🌖", "🌗", "🌘"];
    private static readonly string[] AsciiFrames = ["|", "/", "-", "\\"];

    private readonly bool _enabled;
    private readonly bool _unicode;
    private readonly string[] _frames;
    private readonly string? _previous;

    public TerminalTitle()
    {
        _enabled = Ui.Fancy;
        _unicode = Ui.Err.Profile.Capabilities.Unicode;
        _frames = _unicode ? UnicodeFrames : AsciiFrames;

        // Console.Title's getter is Windows-only; capture it there so we can put
        // the tab back the way we found it. Elsewhere we clear on dispose.
        if (_enabled && OperatingSystem.IsWindows())
        {
            try
            {
                _previous = Console.Title;
            }
            catch
            {
                // Best effort — some hosts refuse the read; we'll just clear later.
            }
        }
    }

    /// <summary>Animated title: the icon advances with <paramref name="frame"/>.</summary>
    public void Animate(int frame, string text) => Write($"{_frames[frame % _frames.Length]} {text}");

    /// <summary>Static "starting up / reconciling" title (hourglass, or ASCII fallback).</summary>
    public void Reconciling(string text) => Write($"{(_unicode ? "⏳" : "*")} {text}");

    public void Dispose() => Write(_previous ?? string.Empty);

    private void Write(string title)
    {
        if (!_enabled)
        {
            return;
        }

        Console.Error.Write(OscPrefix + title + Bel);
    }
}
