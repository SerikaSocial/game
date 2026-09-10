using System.Collections.Generic;

namespace Serika.Script;

/// String pool for passing string arguments through the VM's double-only stack.
/// Scripts reference strings by index; the compiler emits a string table in the SSKB
/// container, and the loader registers them here. Host calls that take string params
/// (shader uniform names, sync var keys, network payloads) receive an index that
/// they resolve through this pool.
///
/// This is an INSTANCE, owned by exactly one ScriptModule. It used to be a static global whose
/// own comment claimed to be per-module; it was not. Two loaded modules shared one pool, which is
/// cross-script reach (threat T5), and `Clear()` on loading a second module invalidated the live
/// indices of the first. Per-script isolation is a sandbox invariant — do not make this static
/// again for the convenience of the call site.
public sealed class StringPool
{
    private readonly List<string> _strings = new();
    private readonly Dictionary<string, int> _lookup = new();

    /// Append a string verbatim and return its index. Used when loading a container's string
    /// table, where index correspondence with the bytecode is mandatory: Register() deduplicates,
    /// so a container carrying the same string twice would collapse the pair and shift every
    /// later index, silently desyncing the pool from what the validator saw.
    public int Append(string s)
    {
        int idx = _strings.Count;
        _strings.Add(s);
        _lookup.TryAdd(s, idx);
        return idx;
    }

    /// Register a string and return its index. Deduplicates by value — for authoring-side use,
    /// never for loading a table whose indices are already baked into bytecode.
    public int Register(string s)
    {
        if (_lookup.TryGetValue(s, out int idx)) return idx;
        idx = _strings.Count;
        _strings.Add(s);
        _lookup[s] = idx;
        return idx;
    }

    /// Get a string by index. Returns empty string for out-of-range indices — an out-of-range
    /// index is a malformed script, and the sandbox answers it with a benign value rather than
    /// an engine lookup.
    public string Get(int index)
    {
        if (index < 0 || index >= _strings.Count) return "";
        return _strings[index];
    }

    /// Number of registered strings.
    public int Count => _strings.Count;
}
