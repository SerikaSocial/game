using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;

namespace SerikaSocial.Discord;

/// <summary>
/// Headless Discord Social SDK diagnostic:
///
///   env -u DISPLAY $GODOT --headless --path game -- --serika-discordtest [--lib <path>] [--wait 8]
///
/// Verifies, in order, without needing a human:
///   1. the native library loads from the shipping search order (env override → beside the
///      exe → extracted from res://bin),
///   2. version queries round-trip through the Discord_String marshal + Discord_Free path,
///   3. a full client lifecycle — init, register launch command, connect, pump, presence
///      push + clear, dispose — completes without a native crash,
///   4. when a Discord client is actually running and logged in, the status timeline reaches
///      Ready and a real presence push is accepted.
///
/// Exit 0 = library + lifecycle healthy (Ready is reported but NOT required — CI machines
/// have no Discord). Exit 1 = the integration itself is broken.
/// </summary>
public static class DiscordDiagnostic
{
    public static async void Run(Node host, string lib, string waitArg)
    {
        int exit = 1;
        try
        {
            exit = await RunAsync(host, lib, waitArg);
        }
        catch (Exception e)
        {
            // async void: an uncaught exception here would vanish and hang headless CI.
            GD.PrintErr($"DISCORDTEST FAIL: {e}");
            host.GetTree().Quit(1);
            return;
        }
        host.GetTree().Quit(exit);
    }

    private static async Task<int> RunAsync(Node host, string lib, string waitArg)
    {
        double waitSecs = 8;
        double.TryParse(waitArg, out double parsed);
        if (parsed >= 1 && parsed <= 120) waitSecs = parsed;

        GD.Print($"DISCORDTEST platform={OS.GetName()} exe={OS.GetExecutablePath()}");
        if (lib != null)
        {
            OS.SetEnvironment("SERIKA_DISCORD_LIB", lib);
            GD.Print($"DISCORDTEST lib override: {lib}");
        }

        // ── 1. native library ──────────────────────────────────────────────────────────
        var sw = Stopwatch.StartNew();
        if (!DiscordNative.EnsureLoaded())
        {
            GD.PrintErr($"DISCORDTEST FAIL: native library not loadable — {DiscordNative.LoadFailure}");
            return 1;
        }
        sw.Stop();
        GD.Print($"DISCORDTEST lib loaded in {sw.ElapsedMilliseconds} ms from: {DiscordNative.LoadedFrom}");
        GD.Print($"DISCORDTEST sdk version: {DiscordClient.VersionString()}");
        GD.Print($"DISCORDTEST bundled version constant: {DiscordNative.SdkVersion} (must match the ref/ drop)");

        // ── 2. client lifecycle ────────────────────────────────────────────────────────
        ulong appId = ulong.TryParse(OS.GetEnvironment("SERIKA_DISCORD_APP_ID"), out ulong id) && id != 0
            ? id
            : DiscordRichPresence.DefaultApplicationId;

        var timeline = new System.Collections.Generic.List<string>();
        bool sawReady = false;
        string authState = "no-token";   // no-token → authorizing → exchanged → applied

        using (var client = new DiscordClient(appId, DiscordNative.LoggingSeverity.Info))
        {
        client.StatusChanged += (status, error, detail) =>
        {
            timeline.Add($"{DiscordClient.StatusName(status)}{(error != DiscordNative.ClientError.None ? $"({error}/{detail})" : "")}");
            if (status == DiscordNative.ClientStatus.Ready) sawReady = true;
        };
        client.LogMessage += (msg, sev) => GD.Print($"DISCORDTEST sdklog[{sev}] {msg}");
        client.JoinSecretReceived += secret => GD.Print($"DISCORDTEST join secret: {secret}");

        bool registered = client.RegisterLaunchCommand($"\"{OS.GetExecutablePath()}\"");
        GD.Print($"DISCORDTEST launch command registered: {registered}");

        // The Social SDK refuses tokenless connects (gateway 4004), so walk the OAuth flow
        // exactly like the game manager: saved token → apply; else Authorize (the Discord
        // client shows a consent popup — clicking it within --wait continues the run).
        var saved = ReadSavedToken(appId);
        if (saved != null)
        {
            authState = "saved-token";
            client.ApplyToken((DiscordNative.AuthorizationTokenType)saved.tokenType, saved.accessToken,
                (ok, error) =>
                {
                    if (!ok) { authState = $"apply-failed({error})"; return; }
                    authState = "applied";
                    client.Connect();
                });
        }
        else
        {
            authState = "authorizing";
            GD.Print("DISCORDTEST requesting authorization — an Authorize prompt should appear in your Discord client");
            client.BeginAuthorize((ok, error, code, redirect) =>
            {
                if (!ok) { authState = $"authorize-failed({error})"; return; }
                authState = "exchanging";
                client.ExchangeCodeForToken(code, redirect, token =>
                {
                    if (!token.Ok) { authState = $"exchange-failed({token.Error})"; return; }
                    authState = "exchanged";
                    WriteSavedToken(appId, token);
                    client.ApplyToken((DiscordNative.AuthorizationTokenType)token.TokenType,
                        token.AccessToken, (ok2, err2) =>
                    {
                        if (!ok2) { authState = $"apply-failed({err2})"; return; }
                        authState = "applied";
                        client.Connect();
                    });
                });
            });
        }

        var pumpSw = Stopwatch.StartNew();
        while (pumpSw.Elapsed.TotalSeconds < waitSecs)
        {
            client.Pump();
            await Task.Delay(50);
        }

        // ── 3. presence push (only meaningful when Ready, harmless otherwise) ──────────
        if (sawReady)
        {
            client.UpdateRichPresence(new ActivitySpec
            {
                Details = "Running the SDK diagnostic",
                State = "1/16 players",
                StartUnixMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                PartyId = "diagnostic",
                PartySize = 1,
                PartyMax = 16,
                JoinSecret = $"{DeepLink.Scheme}://world/diagnostic",
                SmallImage = "world_icon",
                SmallText = "Diagnostic",
                ButtonLabel = "Join Serika Social",
                ButtonUrl = "https://social.serika.dev",
            });
            // Give the async result callback a couple of pumps to land. The callback only
            // logs rejections, so acceptance is observed via silence + final status.
            for (int i = 0; i < 20; i++)
            {
                client.Pump();
                await Task.Delay(50);
            }
            client.ClearRichPresence();
            for (int i = 0; i < 4; i++) { client.Pump(); await Task.Delay(50); }
        }

        string finalStatus = DiscordClient.StatusName(client.Status);
        GD.Print($"DISCORDTEST auth: {authState}");
        GD.Print($"DISCORDTEST status timeline: [{string.Join(" → ", timeline)}] final={finalStatus}");
        GD.Print($"DISCORDTEST discord={(sawReady ? "ready"
            : authState.StartsWith("authoriz") || authState.StartsWith("exchang") || authState == "applied"
                ? "auth-pending (see the Authorize prompt in Discord / check the portal steps)"
                : authState.Contains("failed")
                    ? $"auth-failed ({authState}) — Social SDK not enabled for the app, or Discord not logged in"
                    : "unavailable (informational)")}");
        }

        // Verify a second lifecycle works on a fresh client — disconnect + drop must be
        // repeatable per process, not a one-shot (the reconnect path relies on it).
        using (var second = new DiscordClient(appId))
        {
            second.Connect();
            for (int i = 0; i < 4; i++) { second.Pump(); await Task.Delay(50); }
        }

        GD.Print($"DISCORDTEST PASS (lib=ok lifecycle=ok presence={(sawReady ? "pushed" : "skipped")})");
        return 0;
    }

    // Same file the game manager uses, so an authorization performed by the diagnostic (or
    // vice versa) is reused by both. Mirrors DiscordRichPresence's private DTO.
    private sealed class SavedToken
    {
        public string appId { get; set; }
        public int tokenType { get; set; }
        public string accessToken { get; set; }
        public string refreshToken { get; set; }
        public long expiresUnixMs { get; set; }
    }

    private const string TokenPath = "user://discord_token.json";

    private static SavedToken ReadSavedToken(ulong appId)
    {
        try
        {
            string abs = ProjectSettings.GlobalizePath(TokenPath);
            if (!System.IO.File.Exists(abs)) return null;
            var t = System.Text.Json.JsonSerializer.Deserialize<SavedToken>(System.IO.File.ReadAllText(abs));
            return t.appId == appId.ToString() ? t : null;
        }
        catch { return null; }
    }

    private static void WriteSavedToken(ulong appId, TokenResult token)
    {
        try
        {
            var saved = new SavedToken
            {
                appId = appId.ToString(),
                tokenType = (int)token.TokenType,
                accessToken = token.AccessToken,
                refreshToken = token.RefreshToken,
                expiresUnixMs = token.ExpiresUnixMs,
            };
            System.IO.File.WriteAllText(ProjectSettings.GlobalizePath(TokenPath),
                System.Text.Json.JsonSerializer.Serialize(saved));
        }
        catch (Exception e) { GD.Print($"DISCORDTEST token not saved: {e.Message}"); }
    }
}
