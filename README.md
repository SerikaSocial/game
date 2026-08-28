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
Discord/                   Discord Social SDK: P/Invoke layer, client wrapper, presence manager
Worlds/
```

## Discord Social SDK

Rich presence and Join-on-Discord run on the official **Discord Social SDK 1.10** (the native
C ABI from `cdiscord.h`), P/Invoked from `Discord/DiscordNative.cs` — no hand-rolled IPC
protocol to drift out of date.

- **Binaries** live in `game/bin/` (`libdiscord_partner_sdk.so` / `discord_partner_sdk.dll` /
  `libdiscord_partner_sdk.dylib`), each riding the export's pck via the per-preset
  `include_filter` (same mechanism as ffmpeg). At boot `DiscordNative.EnsureLoaded` extracts
  the platform's copy to `user://bin/` under a **version-stamped name** and loads it via
  `NativeLibrary` — dlopen can't read inside a pck, and the stamp means an SDK upgrade
  replaces the stale extraction on the next boot. Search order: `SERIKA_DISCORD_LIB` env →
  beside the executable → pck extraction → system path.
- **Presence** (`Discord/DiscordRichPresence.cs`): connects at boot, pumps
  `Discord_RunCallbacks` from `Main._Process`, re-pushes the activity on world joins, peer
  count changes, and reconnects. Retries with capped backoff (5 s → 60 s) so a Discord that
  starts after the game is still picked up. Everything is main-thread and failure-disabled —
  no Discord, no library, no crash.
- **Join**: the activity's join secret is the `serikasocial://world/<id>` deep link itself.
  With the game running it arrives in-process (`SetActivityJoinCallback` →
  `Main.OnDiscordJoinRequested` → `JoinWorldById`); cold, Discord launches the registered
  launch command and the same string lands in argv, which `DeepLink.FromCommandLine` already
  parses. Party size/max come from the live relay roster.
- **Config**: `SERIKA_DISCORD_APP_ID` overrides the default application id;
  `SERIKA_DISCORD_LOG=verbose` raises SDK logging to Info.
- **First-run authorization (portal + consent)**: the Social SDK will not authenticate a
  tokenless client — connecting before applying a token is a guaranteed gateway `4004`. The
  app therefore needs `http://127.0.0.1/callback` registered under **Developer Portal → your
  app → OAuth2 → Redirects** (the SDK's built-in loopback redirect; without it authorize dies
  with `OAuth2 Error: invalid_request: Missing "redirect_uri" in request` and no popup ever
  shows). On first boot the manager runs the OAuth flow — saved token → refresh → fresh
  `Authorize` — and Discord shows a **one-time consent popup** in the official client; click
  it, the token is exchanged and persisted to `user://discord_token.json`, and every later
  boot reuses/refreshes it silently.
- **Android**: the SDK ships there as an AAR with Java glue — not wired; the manager
  disables itself on `OS.HasFeature("android")`.
- **arRPC caveat**: an `arRPC` bridge (Vesktop/Vencord setups) answers the local IPC socket
  but only implements the classic `SET_ACTIVITY` protocol — the official SDK's authenticated
  gateway handshake fails there with close code 4004. On such machines Discord presence is
  silently absent (by design — one canonical code path); the official Discord desktop client
  is required for the feature.

Diagnostic (verifies library load, marshal/free, full client lifecycle, and — where a real
Discord client is logged in — a live presence push):

```bash
env -u DISPLAY -u WAYLAND_DISPLAY $GODOT --headless --path game -- --serika-discordtest [--wait 8]
```

## Notes

- C# on Godot 4.x has **no web export**. Browser builds would need a second, non-C# client.
- The VRM importer ([V-Sekai `godot-vrm`](https://github.com/V-Sekai/godot-vrm)) is
  GDScript, so this project is mixed-language by necessity.
- Hot paths must stay allocation-free — `Variant` boxing on every `Set`/signal marshal puts
  the GC squarely in the frame-time path.
