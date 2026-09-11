using System.Text.Json;
using System.Text.Json.Serialization;
using SerikaSocial.Avatar;
using SerikaSocial.Events;

namespace SerikaSocial;

/// AOT-safe JSON for everything the client serializes at runtime.
///
/// iOS ships ahead-of-time compiled (`PublishAot` in the csproj — Apple forbids JIT), and
/// under AOT reflection-based serialization does not exist: a plain
/// `JsonSerializer.Serialize(new { ... })` throws "Reflection-based serialization has been
/// disabled" at runtime. That is how 1.10.3 logged nobody in on iOS while every desktop
/// build was fine — the desktop default resolver papers over exactly the calls an AOT
/// build cannot make. The source generator here emits the serialization metadata at
/// compile time instead.
///
/// Two contexts, because naming policy is baked into generated metadata and cannot be
/// applied afterwards through `JsonSerializerOptions`: `Default` keeps keys exactly as
/// written (the wire format every hand-built payload expects), `Camel` reproduces
/// `LiveEvent.Json`'s camelCase contract with the events API.
///
/// The shared `Options`/`CamelOptions` carry the context resolver and NO reflection
/// fallback, so an unregistered type throws on desktop too — a missing registration fails
/// local testing immediately instead of shipping as an iOS-only breakage. Anonymous types
/// cannot be covered by any source generator: build those payloads as `JsonObject`s.
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SkaMeta))]
[JsonSerializable(typeof(Serika.Net.DefaultHomeResolver.World))]
[JsonSerializable(typeof(Serika.Net.DefaultHomeResolver.Cached))]
internal partial class AotJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LiveEvent))]
[JsonSerializable(typeof(ShowConfig))]
internal partial class AotJsonCamelContext : JsonSerializerContext;

public static class AotJson
{
    /// Default-naming options: byte-identical output to the old reflection calls.
    public static readonly JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = AotJsonContext.Default,
    };

    /// camelCase options: the events API contract (see `LiveEvent.Json`).
    public static readonly JsonSerializerOptions CamelOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = AotJsonCamelContext.Default,
    };

    /// A JSON string literal for embedding in hand-built payloads — the
    /// reflection-free equivalent of what `JsonSerializer.Serialize(string)` did.
    /// The relaxed encoder is what keeps the bytes identical: the DOM default
    /// escapes the inner quote as \u0022 (valid JSON, different bytes), and the
    /// wire contract here is byte-identity, same as the pose codec.
    private static readonly JsonSerializerOptions NodeOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string JStr(string s) =>
        s != null ? System.Text.Json.Nodes.JsonValue.Create(s).ToJsonString(NodeOptions) : "null";
}
