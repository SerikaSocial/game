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

    /// Parent one of the script's DECLARED nodes to a player's bone. Returns true on success.
    ///
    /// This is the only call that touches a player at all, and it is deliberately one-directional:
    /// the attachment rides the player. Nothing here can move, push, teleport or constrain a
    /// player — that capability does not exist in the host API and must not be added here.
    /// Implementations MUST cap the number of simultaneous attachments per script and reject
    /// node slots the script did not declare.
    bool PlayerAttach(int playerIndex, int nodeSlot, int attachPoint);

    /// Return a declared node from a player to the world root. Returns true if it was attached.
    bool PlayerDetach(int nodeSlot);

    /// Emit a message on the relay's rate-limited script channel. The bridge is responsible for
    /// applying the server-enforced rate budget; the VM never touches a socket.
    void NetEmit(int channel, double payload);

    // ── Trust≥5: Custom shader parameters ──
    /// Set a float shader uniform by name on a declared material node.
    void ShaderSetFloat(int nodeSlot, string uniformName, float value);
    /// Set a color (vec4) shader uniform by name.
    void ShaderSetColor(int nodeSlot, string uniformName, float r, float g, float b, float a);
    /// Set a vec4 shader uniform by name.
    void ShaderSetVec4(int nodeSlot, string uniformName, float x, float y, float z, float w);

    // ── Trust≥6: Particle effects and spatial audio ──
    /// Trigger a one-shot particle burst on a declared particle node.
    void ParticleBurst(int nodeSlot);
    /// Set the emission rate of a declared particle node.
    void ParticleSetRate(int nodeSlot, float rate);
    /// Play a spatial sound at a world position.
    void SoundPlaySpatial(int clipSlot, float x, float y, float z);

    // ── Trust≥8: Advanced networking ──
    /// Emit a structured network message with a string payload.
    void NetEmitString(int channel, string payload);
    /// Get a sync var from the relay. Returns 0 if not set.
    double NetSyncGet(string key);
    /// Set a sync var on the relay.
    void NetSyncSet(string key, double value);
}
