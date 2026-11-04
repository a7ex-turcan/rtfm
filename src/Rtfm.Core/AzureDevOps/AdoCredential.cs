using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rtfm.Core.AzureDevOps;

/// <summary>
/// Supplies the <c>Authorization</c> header for Azure DevOps calls (§2.18).
/// Two implementations: <see cref="AzCliCredential"/> (the default — borrow the
/// developer's existing <c>az login</c>) and <see cref="PatCredential"/> (a
/// Personal Access Token from the environment).
/// </summary>
public interface IAdoCredential
{
    /// <summary>
    /// The header to send. <paramref name="forceRefresh"/> asks for a fresh
    /// credential after a 401 — meaningful only when <see cref="CanRefresh"/>.
    /// </summary>
    /// <exception cref="AdoException">The credential could not be obtained (az missing / not logged in, PAT unset).</exception>
    Task<AuthenticationHeaderValue> GetHeaderAsync(bool forceRefresh, CancellationToken cancellationToken);

    /// <summary>True when a rejected credential can be re-acquired (a short-lived token), false for a static PAT.</summary>
    bool CanRefresh { get; }
}

/// <summary>A Personal Access Token, sent as HTTP Basic with an empty user name (<c>:PAT</c>).</summary>
public sealed class PatCredential(string token) : IAdoCredential
{
    private readonly AuthenticationHeaderValue _header =
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + token)));

    public bool CanRefresh => false;

    public Task<AuthenticationHeaderValue> GetHeaderAsync(bool forceRefresh, CancellationToken cancellationToken)
        => Task.FromResult(_header);
}

/// <summary>The outcome of one <c>az</c> invocation.</summary>
internal sealed record AzCliResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// An Entra ID bearer token borrowed from the Azure CLI's logged-in account
/// (<c>az account get-access-token</c>). Nothing secret is stored anywhere: the
/// token is fetched when first needed, held in memory until shortly before it
/// expires (tokens live roughly an hour), and re-fetched after a 401.
///
/// <para><b>The resource id is not one fixed value.</b> Azure DevOps' first-party
/// application id is <c>499b84ac-1321-427f-aa17-267ca4b61b05</c>, and that is
/// what the docs say to request — but in a tenant where that service principal
/// was never provisioned, Entra answers <c>AADSTS500011</c> ("resource principal
/// not found in the tenant"). The same application's legacy identifier URI,
/// <c>https://app.vssps.visualstudio.com</c>, still resolves there and yields a
/// token Azure DevOps accepts (measured on the real org). So unless a resource
/// is configured, both are tried in order and the first that works is kept for
/// the rest of the process.</para>
///
/// <para><b>On Windows <c>az</c> is a batch file</b> (<c>az.cmd</c>), which
/// <c>Process.Start</c> will not find without a shell, so it runs under
/// <c>cmd.exe /d /c</c>. Every argument is validated to a conservative charset
/// first, so nothing reaches the shell that it could interpret.</para>
/// </summary>
public sealed partial class AzCliCredential : IAdoCredential
{
    /// <summary>Azure DevOps' first-party Entra application id.</summary>
    public const string DefaultResource = "499b84ac-1321-427f-aa17-267ca4b61b05";

    /// <summary>The same application's legacy identifier URI — the fallback when the GUID is not provisioned in the tenant.</summary>
    public const string LegacyResource = "https://app.vssps.visualstudio.com";

    // Refresh this long before the stated expiry, so a token never dies mid-crawl.
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);

    private readonly IReadOnlyList<string> _resources;
    private readonly string? _tenant;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<AzCliResult>> _run;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _workingResource;
    private AuthenticationHeaderValue? _header;
    private DateTimeOffset _expiresAt;

    public AzCliCredential(string? resource = null, string? tenant = null)
        : this(resource, tenant, RunAzAsync, () => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>Test seam: inject the <c>az</c> runner and the clock.</summary>
    internal AzCliCredential(
        string? resource,
        string? tenant,
        Func<IReadOnlyList<string>, CancellationToken, Task<AzCliResult>> run,
        Func<DateTimeOffset> now)
    {
        _resources = string.IsNullOrWhiteSpace(resource) ? [DefaultResource, LegacyResource] : [Validate(resource.Trim(), "resource")];
        _tenant = string.IsNullOrWhiteSpace(tenant) ? null : Validate(tenant.Trim(), "tenant");
        _run = run;
        _now = now;
    }

    public bool CanRefresh => true;

    public async Task<AuthenticationHeaderValue> GetHeaderAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _header is not null && _now() < _expiresAt - ExpiryMargin)
            {
                return _header;
            }

            var candidates = _workingResource is { } known ? [known] : _resources;
            string? lastError = null;

            foreach (var resource in candidates)
            {
                var args = new List<string> { "account", "get-access-token", "--resource", resource, "--output", "json" };
                if (_tenant is not null)
                {
                    args.Add("--tenant");
                    args.Add(_tenant);
                }

                var result = await _run(args, cancellationToken).ConfigureAwait(false);
                if (result.ExitCode == 0 && ParseTokenJson(result.StdOut, _now()) is { } token)
                {
                    _workingResource = resource;
                    _header = new AuthenticationHeaderValue("Bearer", token.AccessToken);
                    _expiresAt = token.ExpiresAt;
                    return _header;
                }

                lastError = DescribeFailure(result);

                // Only "this resource isn't known in the tenant" is worth trying the
                // next identifier for; anything else (not logged in, no az) fails
                // the same way for every resource.
                if (!result.StdErr.Contains("AADSTS500011", StringComparison.Ordinal))
                {
                    break;
                }
            }

            throw new AdoException(lastError ?? "could not obtain an Azure DevOps token from the Azure CLI");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads <c>accessToken</c> + expiry from <c>az account get-access-token -o json</c>.
    /// Prefers <c>expires_on</c> (POSIX seconds, unambiguous); falls back to
    /// <c>expiresOn</c>, which older CLIs print as <em>local</em> time with no
    /// zone. With neither, assumes a conservative 30 minutes.
    /// </summary>
    internal static (string AccessToken, DateTimeOffset ExpiresAt)? ParseTokenJson(string json, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("accessToken", out var tokenElement) || tokenElement.GetString() is not { Length: > 0 } token)
            {
                return null;
            }

            if (root.TryGetProperty("expires_on", out var epoch))
            {
                long seconds = 0;
                var parsed = epoch.ValueKind switch
                {
                    JsonValueKind.Number => epoch.TryGetInt64(out seconds),
                    JsonValueKind.String => long.TryParse(epoch.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds),
                    _ => false,
                };

                if (parsed && seconds > 0)
                {
                    return (token, DateTimeOffset.FromUnixTimeSeconds(seconds));
                }
            }

            if (root.TryGetProperty("expiresOn", out var local)
                && DateTime.TryParse(local.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var localTime))
            {
                return (token, new DateTimeOffset(localTime));
            }

            return (token, now + TimeSpan.FromMinutes(30));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A one-line, actionable reason an <c>az</c> call failed.</summary>
    internal static string DescribeFailure(AzCliResult result)
    {
        var stderr = result.StdErr ?? string.Empty;

        // cmd.exe's "is not recognized" (exit 9009) / the Unix runner's synthetic 127.
        if (result.ExitCode is 9009 or 127
            || stderr.Contains("is not recognized", StringComparison.OrdinalIgnoreCase))
        {
            return "the Azure CLI ('az') was not found on PATH — install it and run 'az login', or configure a PAT with --token-env";
        }

        if (stderr.Contains("az login", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("AADSTS70043", StringComparison.Ordinal)
            || stderr.Contains("AADSTS50173", StringComparison.Ordinal)
            || stderr.Contains("InteractionRequired", StringComparison.OrdinalIgnoreCase))
        {
            return "the Azure CLI session needs a fresh sign-in — run 'az login' (add --tenant <id> if the organization lives in another tenant), then retry";
        }

        var firstError = stderr
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            ?? stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

        if (firstError is { Length: > 300 })
        {
            firstError = firstError[..300] + "…";
        }

        return $"az account get-access-token failed (exit {result.ExitCode})" + (firstError is null ? string.Empty : $": {firstError}");
    }

    private static string Validate(string value, string what)
        => SafeArgument().IsMatch(value)
            ? value
            : throw new ArgumentException($"invalid Azure CLI {what} '{value}' — only letters, digits, and . _ : / - are allowed");

    /// <summary>Runs <c>az</c> with fixed, pre-validated arguments. Never throws for a missing binary — reports it as a result.</summary>
    private static async Task<AzCliResult> RunAzAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        ProcessStartInfo info;
        if (OperatingSystem.IsWindows())
        {
            info = new ProcessStartInfo("cmd.exe");
            info.ArgumentList.Add("/d");
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add("az");
        }
        else
        {
            info = new ProcessStartInfo("az");
        }

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new Win32Exception("az did not start");
        }
        catch (Win32Exception)
        {
            return new AzCliResult(127, string.Empty, "az: command not found");
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

            // az's first call in a session can take several seconds; a hung
            // interactive prompt must not hang the crawl forever.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return new AzCliResult(-1, string.Empty, "ERROR: az account get-access-token timed out after 90s");
            }

            return new AzCliResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
    }

    // GUIDs, tenant domains, and resource URIs — nothing a shell would interpret.
    [GeneratedRegex(@"^[A-Za-z0-9._:/\-]+$")]
    private static partial Regex SafeArgument();
}
