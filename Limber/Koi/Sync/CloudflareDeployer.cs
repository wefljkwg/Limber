using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Limber.Koi.Sync;

/// <summary>
/// Deploys the Limber relay to the player's own Cloudflare account through the Cloudflare API, so
/// setup is "paste a token, click Deploy". The API token is only used for these calls and is never saved.
/// </summary>
public sealed class CloudflareDeployer
{
    public const string ScriptName = "limber-relay";
    private const string Api = "https://api.cloudflare.com/client/v4";
    private const string CompatibilityDate = "2025-09-01";

    /// <param name="SubdomainLooksPersonal">The account already had a workers.dev name that Limber didn't
    /// choose (it can be based on the email address). Partners see it in invite codes.</param>
    public sealed record Result(string RelayUrl, string OwnerToken, string AccountId, bool SubdomainLooksPersonal);

    /// <summary>Thrown with a message that can be shown to the user as-is.</summary>
    public sealed class DeployException(string message, string? link = null) : Exception(message)
    {
        public string? Link { get; } = link;
    }

    public static async Task<Result> DeployAsync(string apiToken, string? accountIdHint, IProgress<string> progress, CancellationToken ct)
    {
        apiToken = apiToken.Trim();
        if (apiToken.Length < 20)
            throw new DeployException("That doesn't look like a Cloudflare API token. Copy the whole token Cloudflare shows after \"Create Token\".");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Limber-Deployer/1");

        progress.Report("Checking your token...");
        var verify = await Call(http, HttpMethod.Get, "/user/tokens/verify", null, ct);
        if (!verify.Success)
            throw new DeployException("Cloudflare didn't accept that token. Make a new one with the \"Edit Cloudflare Workers\" template and paste it again.");

        progress.Report("Finding your account...");
        var accountId = accountIdHint?.Trim();
        if (string.IsNullOrEmpty(accountId))
        {
            var accounts = await Call(http, HttpMethod.Get, "/accounts?per_page=5", null, ct);
            accountId = accounts.Result?.AsArray().FirstOrDefault()?["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(accountId))
                throw new DeployException(
                    "Couldn't find your account with this token. Paste your Account ID too (it's in the right-hand column " +
                    "of the Cloudflare dashboard's Workers & Pages page, or in the address bar after dash.cloudflare.com/).");
        }

        progress.Report("Setting up your free workers.dev address...");
        var subdomain = await GetOrCreateSubdomain(http, accountId, ct);

        progress.Report("Uploading the relay...");
        var ownerToken = RandomHex(32);
        // Like Cloudflare's own tool: only send the storage migration if this relay doesn't have it yet.
        var existing = await Call(http, HttpMethod.Get, $"/accounts/{accountId}/workers/services/{ScriptName}", null, ct);
        var migrated = existing.Success &&
                       existing.Result?["default_environment"]?["script"]?["migration_tag"]?.GetValue<string>() == "v1";
        await Upload(http, accountId, ownerToken, !migrated, ct);

        progress.Report("Turning the relay on...");
        var enable = await Call(http, HttpMethod.Post, $"/accounts/{accountId}/workers/scripts/{ScriptName}/subdomain",
            new JsonObject { ["enabled"] = true, ["previews_enabled"] = false }, ct);
        if (!enable.Success)
            throw new DeployException("Uploaded, but Cloudflare wouldn't switch the relay on: " + enable.Error);

        var relayUrl = $"https://{ScriptName}.{subdomain}.workers.dev";

        progress.Report("Waiting for the relay to come online (a brand-new address can take a minute)...");
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        for (int attempt = 0; attempt < 24; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var text = await probe.GetStringAsync(relayUrl + "/health", ct);
                if (text.StartsWith("limber-relay", StringComparison.Ordinal))
                {
                    progress.Report("Relay ready.");
                    return new Result(relayUrl, ownerToken, accountId, Personal(subdomain));
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // DNS for a new workers.dev name may not exist yet; keep trying.
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }

        // Deployed fine; it just isn't reachable yet. Save it anyway - it will start working shortly.
        progress.Report("Relay deployed. It may take a few more minutes before it's reachable.");
        return new Result(relayUrl, ownerToken, accountId, Personal(subdomain));
    }

    // "kpl-" names were made by earlier (KoiPoseLink) builds and are just as anonymous.
    private static bool Personal(string subdomain) =>
        !subdomain.StartsWith("lmb-", StringComparison.Ordinal) && !subdomain.StartsWith("kpl-", StringComparison.Ordinal);

    private static async Task<string> GetOrCreateSubdomain(HttpClient http, string accountId, CancellationToken ct)
    {
        var get = await Call(http, HttpMethod.Get, $"/accounts/{accountId}/workers/subdomain", null, ct);
        var existing = get.Result?["subdomain"]?.GetValue<string>();
        if (get.Success && !string.IsNullOrEmpty(existing))
            return existing;

        // New accounts don't have a workers.dev name yet: pick a random, anonymous one.
        var name = "lmb-" + RandomHex(5);
        var put = await Call(http, HttpMethod.Put, $"/accounts/{accountId}/workers/subdomain",
            new JsonObject { ["subdomain"] = name }, ct);
        var created = put.Result?["subdomain"]?.GetValue<string>();
        if (put.Success && !string.IsNullOrEmpty(created))
            return created;

        throw new DeployException(
            "Your Cloudflare account needs a workers.dev address first. Open the link, click through the short Workers " +
            "setup (it's free), then press Deploy again.",
            $"https://dash.cloudflare.com/{accountId}/workers/onboarding");
    }

    private static async Task Upload(HttpClient http, string accountId, string ownerToken, bool needsMigration, CancellationToken ct)
    {
        var script = LoadRelayScript();

        async Task<ApiResult> Put(bool withMigration)
        {
            var metadata = new JsonObject
            {
                ["main_module"] = "relay.js",
                ["compatibility_date"] = CompatibilityDate,
                ["bindings"] = new JsonArray
                {
                    new JsonObject { ["type"] = "durable_object_namespace", ["name"] = "RELAY", ["class_name"] = "Relay" },
                    new JsonObject { ["type"] = "secret_text", ["name"] = "OWNER_TOKEN", ["text"] = ownerToken },
                },
            };
            if (withMigration)
            {
                metadata["migrations"] = new JsonObject
                {
                    ["new_tag"] = "v1",
                    ["steps"] = new JsonArray { new JsonObject { ["new_sqlite_classes"] = new JsonArray { "Relay" } } },
                };
            }

            using var form = new MultipartFormDataContent();
            var meta = new StringContent(metadata.ToJsonString(), Encoding.UTF8);
            meta.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            form.Add(meta, "metadata");
            var module = new ByteArrayContent(Encoding.UTF8.GetBytes(script));
            module.Headers.ContentType = new MediaTypeHeaderValue("application/javascript+module");
            form.Add(module, "relay.js", "relay.js");

            using var request = new HttpRequestMessage(HttpMethod.Put, $"{Api}/accounts/{accountId}/workers/scripts/{ScriptName}") { Content = form };
            using var response = await http.SendAsync(request, ct);
            return await ApiResult.Read(response, ct);
        }

        var first = await Put(withMigration: needsMigration);
        if (first.Success)
            return;

        // Re-deploying over an existing relay: the storage migration is already applied, upload without it.
        if (first.Error.Contains("migration", StringComparison.OrdinalIgnoreCase))
        {
            var second = await Put(withMigration: !needsMigration);
            if (second.Success)
                return;
            throw new DeployException("Cloudflare rejected the relay upload: " + second.Error);
        }
        throw new DeployException("Cloudflare rejected the relay upload: " + first.Error);
    }

    private static string LoadRelayScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("Limber.relay.js")
                           ?? throw new DeployException("The relay script is missing from this build of Limber.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static string RandomHex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    // ----------------------------------------------------------------------------------------

    private sealed record ApiResult(bool Success, JsonNode? Result, string Error)
    {
        public static async Task<ApiResult> Read(HttpResponseMessage response, CancellationToken ct)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            try
            {
                var json = JsonNode.Parse(body);
                var ok = json?["success"]?.GetValue<bool>() ?? false;
                var errors = json?["errors"]?.AsArray()
                    .Select(e => $"{e?["message"]?.GetValue<string>()} ({e?["code"]})")
                    .ToArray() ?? [];
                return new ApiResult(ok && response.IsSuccessStatusCode, json?["result"],
                    errors.Length > 0 ? string.Join("; ", errors) : $"HTTP {(int)response.StatusCode}");
            }
            catch (JsonException)
            {
                return new ApiResult(false, null, $"HTTP {(int)response.StatusCode}");
            }
        }
    }

    private static async Task<ApiResult> Call(HttpClient http, HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Api + path);
        if (body != null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        return await ApiResult.Read(response, ct);
    }
}
