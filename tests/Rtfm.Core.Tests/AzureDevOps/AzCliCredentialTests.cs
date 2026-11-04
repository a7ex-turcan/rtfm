using Rtfm.Core.AzureDevOps;

namespace Rtfm.Core.Tests.AzureDevOps;

/// <summary>
/// The Azure CLI credential, with the <c>az</c> process replaced by a scripted
/// runner — no process is started, no network touched.
/// </summary>
public class AzCliCredentialTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static string TokenJson(string token, DateTimeOffset expires)
        => $$"""{"accessToken":"{{token}}","expires_on":{{expires.ToUnixTimeSeconds()}},"tokenType":"Bearer"}""";

    private sealed class ScriptedAz
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Func<IReadOnlyList<string>, AzCliResult> Respond { get; set; } = _ => new AzCliResult(1, "", "ERROR: unscripted");

        public Task<AzCliResult> RunAsync(IReadOnlyList<string> args, CancellationToken _)
        {
            Calls.Add(args);
            return Task.FromResult(Respond(args));
        }

        public static string ResourceOf(IReadOnlyList<string> args) => args[args.ToList().IndexOf("--resource") + 1];
    }

    [Fact]
    public async Task Falls_back_to_the_legacy_resource_when_the_guid_is_not_in_the_tenant()
    {
        // The real-tenant finding: the GUID answers AADSTS500011, the URI works.
        var az = new ScriptedAz
        {
            Respond = args => ScriptedAz.ResourceOf(args) == AzCliCredential.DefaultResource
                ? new AzCliResult(1, "", "ERROR: AADSTS500011: The resource principal named 499b84ac... was not found in the tenant")
                : new AzCliResult(0, TokenJson("legacy-token", Now.AddHours(1)), ""),
        };
        var credential = new AzCliCredential(null, null, az.RunAsync, () => Now);

        var header = await credential.GetHeaderAsync(forceRefresh: false, default);

        Assert.Equal("Bearer", header.Scheme);
        Assert.Equal("legacy-token", header.Parameter);
        Assert.Equal([AzCliCredential.DefaultResource, AzCliCredential.LegacyResource], az.Calls.Select(ScriptedAz.ResourceOf));

        // The working resource is remembered: a refresh goes straight to it.
        await credential.GetHeaderAsync(forceRefresh: true, default);
        Assert.Equal(AzCliCredential.LegacyResource, ScriptedAz.ResourceOf(az.Calls[^1]));
        Assert.Equal(3, az.Calls.Count);
    }

    [Fact]
    public async Task Caches_until_near_expiry_then_refetches()
    {
        var clock = Now;
        var issued = 0;
        var az = new ScriptedAz { Respond = _ => new AzCliResult(0, TokenJson($"t{++issued}", clock.AddMinutes(60)), "") };
        var credential = new AzCliCredential(null, null, az.RunAsync, () => clock);

        Assert.Equal("t1", (await credential.GetHeaderAsync(false, default)).Parameter);
        Assert.Equal("t1", (await credential.GetHeaderAsync(false, default)).Parameter);
        Assert.Single(az.Calls);

        // Inside the 5-minute margin → refetch.
        clock = Now.AddMinutes(56);
        Assert.Equal("t2", (await credential.GetHeaderAsync(false, default)).Parameter);
        Assert.Equal(2, az.Calls.Count);
    }

    [Fact]
    public async Task A_sign_in_failure_does_not_retry_other_resources_and_says_az_login()
    {
        var az = new ScriptedAz
        {
            Respond = _ => new AzCliResult(1, "", "ERROR: AADSTS70043: The refresh token has expired... Run the command below to authenticate interactively: az login"),
        };
        var credential = new AzCliCredential(null, null, az.RunAsync, () => Now);

        var ex = await Assert.ThrowsAsync<AdoException>(() => credential.GetHeaderAsync(false, default));
        Assert.Contains("az login", ex.Message);
        Assert.Single(az.Calls);
    }

    [Fact]
    public async Task Configured_resource_and_tenant_are_passed_and_no_fallback_is_tried()
    {
        var az = new ScriptedAz { Respond = _ => new AzCliResult(1, "", "ERROR: AADSTS500011: not found") };
        var credential = new AzCliCredential("https://custom.example/", "contoso.onmicrosoft.com", az.RunAsync, () => Now);

        await Assert.ThrowsAsync<AdoException>(() => credential.GetHeaderAsync(false, default));

        var args = Assert.Single(az.Calls);
        Assert.Equal("https://custom.example/", ScriptedAz.ResourceOf(args));
        Assert.Equal("contoso.onmicrosoft.com", args[args.ToList().IndexOf("--tenant") + 1]);
    }

    [Theory]
    [InlineData("x & calc")]
    [InlineData("a|b")]
    [InlineData("\"quoted\"")]
    [InlineData("%PATH%")]
    public void Arguments_a_shell_could_interpret_are_rejected(string resource)
        => Assert.Throws<ArgumentException>(() => new AzCliCredential(resource, null, (_, _) => Task.FromResult(new AzCliResult(0, "", "")), () => Now));

    [Fact]
    public void ParseTokenJson_prefers_the_unambiguous_epoch()
    {
        var expires = Now.AddMinutes(42);
        var parsed = AzCliCredential.ParseTokenJson(
            $$"""{"accessToken":"abc","expiresOn":"1999-01-01 00:00:00.000000","expires_on":"{{expires.ToUnixTimeSeconds()}}"}""", Now);

        Assert.NotNull(parsed);
        Assert.Equal("abc", parsed.Value.AccessToken);
        Assert.Equal(expires, parsed.Value.ExpiresAt);
    }

    [Fact]
    public void ParseTokenJson_falls_back_to_local_expiresOn_then_to_thirty_minutes()
    {
        var local = AzCliCredential.ParseTokenJson("""{"accessToken":"abc","expiresOn":"2026-11-04 14:49:16.000000"}""", Now);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 11, 4, 14, 49, 16, DateTimeKind.Local)), local!.Value.ExpiresAt);

        var none = AzCliCredential.ParseTokenJson("""{"accessToken":"abc"}""", Now);
        Assert.Equal(Now.AddMinutes(30), none!.Value.ExpiresAt);

        Assert.Null(AzCliCredential.ParseTokenJson("""{"tokenType":"Bearer"}""", Now));
        Assert.Null(AzCliCredential.ParseTokenJson("not json", Now));
    }

    [Theory]
    [InlineData(9009, "'az' is not recognized as an internal or external command", "not found on PATH")]
    [InlineData(127, "az: command not found", "not found on PATH")]
    [InlineData(1, "ERROR: Please run 'az login' to setup account.", "az login")]
    [InlineData(1, "ERROR: something else broke\nmore detail", "something else broke")]
    public void DescribeFailure_is_actionable(int exit, string stderr, string expected)
        => Assert.Contains(expected, AzCliCredential.DescribeFailure(new AzCliResult(exit, "", stderr)));
}
