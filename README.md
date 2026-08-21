# Serika Social — game client

Godot 4.7 (C#/Mono) client for [Serika Social](https://github.com/SerikaSocial).

## Setup

`proto` is a submodule and the codec tests won't run without it:

```bash
git clone --recurse-submodules https://github.com/SerikaSocial/game.git
# already cloned?
git submodule update --init
```

**Godot binary** (local dev on this machine):

```bash
GODOT=/media/pikachubolk/63d7930c-4cfb-4c68-96a6-879048200e36/Documents/Godot/Godot_v4.7.1-stable_mono_linux_x86_64/Godot_v4.7.1-stable_mono_linux.x86_64
$GODOT --headless --build    # build C# solution
$GODOT --headless --export-release "Linux/X11"   # export a platform
```

## The proto submodule

`proto` holds the wire codec spec and its golden corpus — the contract with the Rust relay
in [`server`](https://github.com/SerikaSocial/server). The C# codec in `Net/Codec/` must
encode byte-for-byte identically to the Rust implementation, and `Net/Codec/GoldenTests.cs`
asserts that against `proto/golden/vectors.json`.

Both repos pin the same `proto` commit. When the protocol changes, `proto` is pushed first,
then both pins move together. A client and a relay on different `proto` commits is exactly
the desync the corpus exists to prevent.

Read `proto/pose_codec.md` before touching anything in `Net/Codec/`.

## Planned structure

```
Net/
  ISerikaTransport.cs      transport abstraction — ENet now, WebRTC for P2P later
  EnetTransport.cs
  Codec/                   C# side of the wire format + golden tests
Avatar/                    humanoid rig, LOD tiers, remote interpolation
Auth/                      OAuth PKCE, loopback listener on fixed port 34517
Worlds/
```

## Notes

- C# on Godot 4.x has **no web export**. Browser builds would need a second, non-C# client.
- The VRM importer ([V-Sekai `godot-vrm`](https://github.com/V-Sekai/godot-vrm)) is
  GDScript, so this project is mixed-language by necessity.
- Hot paths must stay allocation-free — `Variant` boxing on every `Set`/signal marshal puts
  the GC squarely in the frame-time path.
