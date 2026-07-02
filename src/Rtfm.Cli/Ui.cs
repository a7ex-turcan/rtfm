using Spectre.Console;

namespace Rtfm.Cli;

/// <summary>
/// Shared presentation plumbing for the CLI (Phase 7). Two consoles, matching
/// the pre-existing stream conventions: <see cref="Out"/> (stdout) carries
/// *results* (search hits, help, ping report), <see cref="Err"/> (stderr)
/// carries *diagnostics* (index progress, watch dashboard) — so redirecting
/// stdout still yields exactly the machine-consumable half. Spectre strips
/// colors/ANSI on redirected streams by itself; <see cref="Fancy"/> gates the
/// layouts that only make sense on a live terminal (progress, live dashboard).
/// </summary>
internal static class Ui
{
    /// <summary>RTFM's accent color (manuals are orange, everyone knows that).</summary>
    public static readonly Color Accent = Color.Orange1;

    public static IAnsiConsole Out { get; } = AnsiConsole.Console;

    public static IAnsiConsole Err { get; } = AnsiConsole.Create(new AnsiConsoleSettings
    {
        Out = new AnsiConsoleOutput(Console.Error),
    });

    /// <summary>True when stderr is a live terminal (drives progress/live rendering).</summary>
    public static bool Fancy => Err.Profile.Capabilities.Interactive;

    /// <summary>Markup-safe text.</summary>
    public static string E(string? text) => Markup.Escape(text ?? string.Empty);
}
