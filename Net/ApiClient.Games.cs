using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using SerikaSocial;

namespace Serika.Net;

/// Client half of the server-authoritative game session (`/v1/games`).
///
/// Every method here is a REQUEST, never a decision. The server owns roles, kills, votes and win
/// conditions, and it will refuse anything the rules disallow — this class exists to ask and to
/// report what the answer was. In particular `GetMeAsync` returns only the caller's OWN role:
/// there is no endpoint that returns the role map while a game is live, by design, because a
/// client that receives the imposter's identity has already leaked it.
public sealed partial class ApiClient
{
    /// Start a session on an instance. Host only, server-enforced. `mode` 0 = imposter, 1 = gauntlet, 2 = rope.
    public async Task<JsonElement> StartGameAsync(string instanceId, int mode, int rounds = 3) =>
        await PostAuthedAsync($"/v1/games/{instanceId}/start",
            new JsonObject { ["mode"] = mode, ["rounds"] = rounds }.ToJsonString());

    /// What this player is allowed to know about themselves.
    public async Task<JsonElement> GetGameMeAsync(string instanceId) =>
        await GetGameJsonAsync($"/v1/games/{instanceId}/me");

    /// Phase, counts and the public event log. Never the role map while the game is live.
    public async Task<JsonElement> GetGameStateAsync(string instanceId) =>
        await GetGameJsonAsync($"/v1/games/{instanceId}/state");

    public async Task<JsonElement> CompleteTaskAsync(string instanceId, int taskId) =>
        await PostAuthedAsync($"/v1/games/{instanceId}/task",
            new JsonObject { ["taskId"] = taskId }.ToJsonString());

    /// Ask to kill. `distance` is our own reported separation — the server treats it as untrusted
    /// (same posture as `rms` in the pose codec) and uses it only as a sanity bound.
    public async Task<JsonElement> KillAsync(string instanceId, string targetId, float distance) =>
        await PostAuthedAsync($"/v1/games/{instanceId}/kill",
            new JsonObject { ["targetId"] = targetId, ["distance"] = distance }.ToJsonString());

    public async Task<JsonElement> ReportBodyAsync(string instanceId) =>
        await PostAuthedAsync($"/v1/games/{instanceId}/report", "{}");

    /// `targetId` may be the literal "skip".
    public async Task<JsonElement> VoteAsync(string instanceId, string targetId) =>
        await PostAuthedAsync($"/v1/games/{instanceId}/vote",
            new JsonObject { ["targetId"] = targetId }.ToJsonString());

    public async Task<JsonElement> CloseMeetingAsync(string instanceId) =>
        await PostAuthedAsync($"/v1/games/{instanceId}/close-meeting", "{}");

    /// Gauntlet: report crossing the line. The SERVER assigns the placement by arrival order, so
    /// this cannot claim a position.
    public async Task<JsonElement> ReportFinishAsync(string instanceId, int round, long startedAt) =>
        await PostAuthedAsync($"/v1/games/{instanceId}/finish",
            new JsonObject { ["round"] = round, ["startedAt"] = startedAt }.ToJsonString());

    public async Task<JsonElement> AbortGameAsync(string instanceId) =>
        await PostAuthedAsync($"/v1/games/{instanceId}/abort", "{}");

    public async Task<JsonElement> EndRoundAsync(string instanceId) =>
        await PostAuthedAsync($"/v1/games/{instanceId}/end-round", "{}");
    private async Task<JsonElement> GetGameJsonAsync(string path)
    {
        var json = await GetAuthedAsync(path);
        if (json.TryGetProperty("error", out var error))
            throw new InvalidOperationException(error.GetString());
        return json;
    }

}
