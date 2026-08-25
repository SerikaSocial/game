using System;
using Serika.Net;
using Xunit;

namespace Serika.Net.Tests;

/// ObjectSync/PhysGrab framing.
///
/// These messages are not in the golden corpus, because the relay never decodes them — it
/// length-checks and copies the body verbatim. What *does* need pinning down is that the same
/// PhysGrab bytes mean different things on the two transports: over the relay the server strips
/// `target_peer` and re-frames with a sender id, while a direct P2P data channel has no server to
/// do that. Reading one layout with the other's parser silently shifts every field after the first
/// byte by four, which is the kind of bug that survives a code review and shows up as "grabbing
/// someone's hair teleports it".
public class ObjectFrameTests
{
    [Fact]
    public void ObjectSync_RoundTrips()
    {
        var frame = ObjectFrames.WriteObjectSync(
            0x1234, 1.5f, -2.25f, 3.75f, 0.1f, 0.2f, 0.3f, 0.927f, -4f, 5f, -6f);

        Assert.Equal(ObjectFrames.ObjectSyncSize, frame.Length);

        Assert.True(ObjectFrames.ReadObjectSync(frame, out var objId,
            out var x, out var y, out var z,
            out var qx, out var qy, out var qz, out var qw,
            out var lvx, out var lvy, out var lvz));

        Assert.Equal(0x1234, objId);
        Assert.Equal(1.5f, x); Assert.Equal(-2.25f, y); Assert.Equal(3.75f, z);
        Assert.Equal(0.1f, qx); Assert.Equal(0.2f, qy); Assert.Equal(0.3f, qz); Assert.Equal(0.927f, qw);
        Assert.Equal(-4f, lvx); Assert.Equal(5f, lvy); Assert.Equal(-6f, lvz);
    }

    /// The relay's guard is `payload.len() < 44`, so the buffer must be 44 even though only 42
    /// bytes carry content. A 42-byte frame would be dropped by the relay without a word.
    [Fact]
    public void ObjectSync_IsPaddedToTheLengthTheRelayChecks()
    {
        var frame = ObjectFrames.WriteObjectSync(1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0);
        Assert.Equal(44, frame.Length);
        Assert.Equal(0, frame[42]);
        Assert.Equal(0, frame[43]);
    }

    [Fact]
    public void ObjectSync_RejectsShortBody()
    {
        var truncated = new byte[43];
        Assert.False(ObjectFrames.ReadObjectSync(truncated, out _,
            out _, out _, out _, out _, out _, out _, out _, out _, out _, out _));
    }

    /// P2P path: what SendPhysGrab writes is what WebRtcTransport reads, target_peer included.
    [Fact]
    public void PhysGrab_RoundTripsOnDirectChannel()
    {
        var frame = ObjectFrames.WritePhysGrab(2, targetPeer: 0xDEADBEEF, boneOrObjId: 0x0777,
            x: 1f, y: 2f, z: 3f);

        Assert.Equal(ObjectFrames.PhysGrabSendSize, frame.Length);

        Assert.True(ObjectFrames.ReadPhysGrab(frame, hasTargetPeer: true,
            out var grabType, out var id, out var x, out var y, out var z));

        Assert.Equal(2, grabType);
        Assert.Equal(0x0777, id);
        Assert.Equal(1f, x); Assert.Equal(2f, y); Assert.Equal(3f, z);
    }

    /// Relay path: the server drops target_peer, so the client reads the shorter layout. This
    /// simulates that by slicing the field out exactly as `handle_phys_grab` does.
    [Fact]
    public void PhysGrab_RoundTripsAfterRelayStripsTargetPeer()
    {
        var sent = ObjectFrames.WritePhysGrab(1, targetPeer: 0xDEADBEEF, boneOrObjId: 0x0042,
            x: -1.5f, y: 0.25f, z: 9f);

        // server.rs: `let rest = &payload[5..]` then re-frame with the sender id prepended.
        var relayed = new byte[ObjectFrames.PhysGrabRelayedSize];
        relayed[0] = sent[0];
        Array.Copy(sent, 5, relayed, 1, sent.Length - 5);

        Assert.True(ObjectFrames.ReadPhysGrab(relayed, hasTargetPeer: false,
            out var grabType, out var id, out var x, out var y, out var z));

        Assert.Equal(1, grabType);
        Assert.Equal(0x0042, id);
        Assert.Equal(-1.5f, x); Assert.Equal(0.25f, y); Assert.Equal(9f, z);
    }

    /// The whole reason `hasTargetPeer` exists. Reading a direct-channel frame with the relay
    /// parser must not quietly produce plausible-looking garbage.
    [Fact]
    public void PhysGrab_WrongLayoutDoesNotDecodeToTheSameValues()
    {
        var direct = ObjectFrames.WritePhysGrab(0, targetPeer: 0xDEADBEEF, boneOrObjId: 0x0042,
            x: -1.5f, y: 0.25f, z: 9f);

        Assert.True(ObjectFrames.ReadPhysGrab(direct, hasTargetPeer: false,
            out _, out var id, out var x, out _, out _));

        Assert.NotEqual(0x0042, id);
        Assert.NotEqual(-1.5f, x);
    }
}
