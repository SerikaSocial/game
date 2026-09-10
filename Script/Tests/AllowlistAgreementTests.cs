using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Serika.Script;
using Xunit;

namespace Serika.Script.Tests;

/// The sandbox boundary is an AGREEMENT between two independent implementations: the server
/// validator (server/api/src/serikascript.ts) decides what may be published, and the client VM
/// (game/Script/) decides what may execute. Threat T7 is the two disagreeing — the client
/// accepting what the server rejected, or vice versa.
///
/// docs/serikascript.md gate 2 requires a golden test proving they agree. There wasn't one, and
/// the tables had silently diverged by nine host calls (0x0600-0x0802 existed only in C#), plus a
/// whole trust-gating model the validator had never heard of. So this suite reads the TypeScript
/// as DATA and compares it against the C# enums. It deliberately does not re-state the tables —
/// a test that hard-codes its own copy of an allowlist just adds a third thing to drift.
public class AllowlistAgreementTests
{
    private static string ValidatorSource => File.ReadAllText(FindValidator());

    /// Walk up from the test assembly to the repo root and locate the validator. Keeps the test
    /// working regardless of the build output directory depth.
    private static string FindValidator()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "server", "api", "src", "serikascript.ts");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "could not locate server/api/src/serikascript.ts by walking up from " + AppContext.BaseDirectory);
    }

    /// Pull `NAME: 0x1234,` pairs out of a named `export const X = { ... } as const;` block.
    private static Dictionary<string, int> ParseTsTable(string source, string constName)
    {
        var block = Regex.Match(
            source,
            @"export\s+const\s+" + Regex.Escape(constName) + @"\s*=\s*\{(.*?)\}\s*as\s+const\s*;",
            RegexOptions.Singleline);
        Assert.True(block.Success, $"could not find `export const {constName}` in the validator");

        var table = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(block.Groups[1].Value, @"(\w+)\s*:\s*(0x[0-9a-fA-F]+)"))
            table[m.Groups[1].Value] = Convert.ToInt32(m.Groups[2].Value, 16);
        return table;
    }

    /// TS spells ids SCREAMING_SNAKE, C# spells them PascalCase. Compare on a shape both agree on.
    private static string Normalize(string name) => name.Replace("_", "").ToLowerInvariant();

    private static Dictionary<string, int> CsharpEnum<TEnum>() where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().ToDictionary(
            v => Normalize(v.ToString()),
            v => Convert.ToInt32(v));

    private static void AssertTablesMatch(Dictionary<string, int> ts, Dictionary<string, int> cs, string what)
    {
        var tsNorm = ts.ToDictionary(kv => Normalize(kv.Key), kv => kv.Value);

        var onlyInCs = cs.Keys.Except(tsNorm.Keys).OrderBy(k => k).ToList();
        var onlyInTs = tsNorm.Keys.Except(cs.Keys).OrderBy(k => k).ToList();

        Assert.True(onlyInCs.Count == 0,
            $"{what} present in the C# VM but NOT on the server allowlist (client would execute what " +
            $"the server never validated): {string.Join(", ", onlyInCs)}");
        Assert.True(onlyInTs.Count == 0,
            $"{what} present on the server allowlist but NOT in the C# VM (server would publish what " +
            $"the client cannot run): {string.Join(", ", onlyInTs)}");

        foreach (var (name, tsValue) in tsNorm.OrderBy(kv => kv.Key))
            Assert.True(cs[name] == tsValue,
                $"{what} `{name}` has id 0x{tsValue:x} on the server but 0x{cs[name]:x} in the VM");
    }

    [Fact]
    public void OpcodeTablesAgree()
    {
        AssertTablesMatch(ParseTsTable(ValidatorSource, "OpCode"), CsharpEnum<OpCode>(), "opcode");
    }

    [Fact]
    public void HostCallTablesAgree()
    {
        AssertTablesMatch(ParseTsTable(ValidatorSource, "HostCall"), CsharpEnum<HostCall>(), "host call");
    }

    /// Hook ids and their arities are a third shared table, and dispatch depends on both sides
    /// agreeing: a hook the compiler emits at one id and the VM dispatches at another simply never
    /// runs, and an arity mismatch feeds a hook garbage in its locals.
    [Fact]
    public void HookTablesAgree()
    {
        AssertTablesMatch(ParseTsTable(ValidatorSource, "HookId"), CsharpEnum<HookId>(), "hook");

        var block = Regex.Match(
            ValidatorSource,
            @"export\s+const\s+HOOK_ARITY\s*:[^=]*=\s*\{(.*?)\}\s*;",
            RegexOptions.Singleline);
        Assert.True(block.Success, "could not find `export const HOOK_ARITY` in the validator");

        var tsArity = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(block.Groups[1].Value, @"\[HookId\.(\w+)\]\s*:\s*(\d+)"))
            tsArity[Normalize(m.Groups[1].Value)] = int.Parse(m.Groups[2].Value);

        foreach (var hook in Enum.GetValues<HookId>())
        {
            string name = Normalize(hook.ToString());
            Assert.True(tsArity.ContainsKey(name), $"HOOK_ARITY on the server is missing `{name}`");
            Assert.True(HookArity.Of(hook) == tsArity[name],
                $"hook `{name}` takes {HookArity.Of(hook)} args in the VM but {tsArity[name]} on the server");
        }
    }

    /// The container version is the shape both parsers agree on; a mismatch means one side is
    /// reading a layout the other never wrote.
    [Fact]
    public void ContainerVersionsAgree()
    {
        var m = Regex.Match(ValidatorSource, @"export\s+const\s+SSKB_VERSION\s*=\s*(\d+)");
        Assert.True(m.Success, "could not find SSKB_VERSION in the validator");
        Assert.Equal(ScriptModule.Version, int.Parse(m.Groups[1].Value));
    }

    /// The trust floors are as load-bearing as the ids: a capability the server gates at rank 8 but
    /// the client hands to rank 0 is a privilege escalation, and the reverse silently bricks a
    /// legitimately published world. This half of the agreement existed only in C# until now.
    [Fact]
    public void TrustFloorsAgree()
    {
        var source = ValidatorSource;
        var hostCalls = ParseTsTable(source, "HostCall");

        var block = Regex.Match(
            source,
            @"export\s+const\s+HOST_CALL_MIN_TRUST\s*:[^=]*=\s*\{(.*?)\}\s*;",
            RegexOptions.Singleline);
        Assert.True(block.Success, "could not find `export const HOST_CALL_MIN_TRUST` in the validator");

        // Every call the server gates, at the level it gates it.
        var tsTrust = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(block.Groups[1].Value, @"\[HostCall\.(\w+)\]\s*:\s*(\d+)"))
        {
            Assert.True(hostCalls.ContainsKey(m.Groups[1].Value),
                $"HOST_CALL_MIN_TRUST references unknown host call {m.Groups[1].Value}");
            tsTrust[Normalize(m.Groups[1].Value)] = int.Parse(m.Groups[2].Value);
        }

        // Compare against the C# floor for EVERY host call, so a gate added on one side only —
        // in either direction — fails here rather than in production.
        foreach (var call in Enum.GetValues<HostCall>())
        {
            string name = Normalize(call.ToString());
            int cs = HostCallTrust.MinTrust(call);
            int ts = tsTrust.TryGetValue(name, out int t) ? t : 0;
            Assert.True(cs == ts,
                $"host call `{name}` requires trust {cs} in the VM but {ts} on the server");
        }
    }
}
