namespace Serika.Script;

/// The engine-facing side of the sandbox. The VM only ever reaches the world through this
/// interface — there is no other bridge. A Godot implementation (ScriptHostBridge, wired to the
/// script's declared node subtree only) lives in the game layer; the pure-C# VM and its tests use
/// a trivial fake. Every method here corresponds to an allowlisted HostCall.
///
/// Implementations MUST enforce scope: node ids are indices into the script's *declared* node
/// table, never arbitrary paths. Anything out of range is a no-op (never an engine lookup).
public interface IHostBridge
{
    void Log(string message);
    double Time();
    double Random();

    void NodeMove(int nodeSlot, float x, float y, float z);
    void NodeRotate(int nodeSlot, float x, float y, float z);
    void NodeSetVisible(int nodeSlot, bool visible);
    void NodePlayAnim(int nodeSlot, int animId);

    void SoundPlay(int clipSlot);
    void ScreenSetText(int screenSlot, string text);

    int PlayerCount();
    (float X, float Y, float Z) PlayerPos(int playerIndex);

    /// Emit a message on the relay's rate-limited script channel. The bridge is responsible for
    /// applying the server-enforced rate budget; the VM never touches a socket.
    void NetEmit(int channel, double payload);
}
