using System.Collections.Generic;

namespace Serika.Script;

/// String pool for passing string arguments through the VM's double-only stack.
/// Scripts reference strings by index; the compiler emits a string table in the SSKB
/// header, and the loader registers them here. Host calls that take string params
/// (shader uniform names, sync var keys, network payloads) receive an index that
/// they resolve through this pool.
///
/// The pool is per-module (cleared on load) to prevent cross-module string leakage.
public static class StringPool
{
    private static readonly List<string> _strings = new();
    private static readonly Dictionary<string, int> _lookup = new();

    /// Clear the pool. Called at module load time.
    public static void Clear()
    {
        _strings.Clear();
        _lookup.Clear();
    }

    /// Register a string and return its index. Deduplicates by value.
    public static int Register(string s)
    {
        if (_lookup.TryGetValue(s, out int idx)) return idx;
        idx = _strings.Count;
        _strings.Add(s);
        _lookup[s] = idx;
        return idx;
    }

    /// Get a string by index. Returns empty string for out-of-range indices.
    public static string Get(int index)
    {
        if (index < 0 || index >= _strings.Count) return "";
        return _strings[index];
    }

    /// Number of registered strings.
    public static int Count => _strings.Count;
}
