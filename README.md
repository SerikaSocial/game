# Serika Social — game client

The Godot 4.7 (C#/Mono) client for [Serika Social](https://github.com/SerikaSocial),
a social VR platform in the VRChat mould: user-uploaded avatars and worlds, spatial
voice, small instances peer-to-peer and large ones on dedicated Rust relays.

Current version: **1.8.14** (see `project.godot`). Login, Home, The Commons
(multiplayer), text chat, humanoid avatars, VR (OpenXR), mobile touch controls,
events/concerts, and the Discord Social SDK all work end-to-end against production.

> For exhaustive operational rules, gotchas, and the full diagnostic catalogue, see
> [`AGENTS.md`](https://github.com/SerikaSocial/game/blob/main/AGENTS.md) (mirrored as
> `CLAUDE.md`). This README is the contributor onboarding doc; `AGENTS.md` is the
> source of truth for "why things are the way they are".

## Setup

`proto` is a submodule and the codec tests won't build without it:

```bash
git clone --recurse-submodules https://github.com/SerikaSocial/game.git
# already cloned?
git submodule update --init
```

You need the **Godot 4.7 mono** binary on your machine and the .NET SDK. Godot is not
on PATH on the dev machine — use the full binary path:

```bash
GODOT=/path/to/Godot_v4.7.1-stable_mono_linux.x86_64

cd game
dotnet build                                       # build the C# solution
$GODOT --headless --build                         # alt: build via Godot
$GODOT --headless --export-release "Linux/X11"  ../dist/linux/SerikaSocial.x86_64
$GODOT --headless --export-release "Windows Desktop" ../dist/windows/SerikaSocial.exe
$GODOT --headless --export-release "macOS"         ../dist/macos/SerikaSocial.zip
$GODOT --headless --export-release "Android (Quest)"   ../dist/android/SerikaSocial.apk
$GODOT --headless --export-release "Android (Mobile)" ../dist/android-mobile/SerikaSocial.apk
```

Export presets live in `export_presets.cfg`. Android exports need the release keystore
(local, not in this repo).

## The proto submodule

`proto` holds the wire codec spec and its golden corpus — the contract with the Rust
relay in [`server`](https://github.com/SerikaSocial/server). The C# codec in
`Net/Codec/` must encode byte-for-byte identically to the Rust implementation, and
`Net/Codec/Tests/GoldenTests.cs` asserts that against `proto/golden/vectors.json`.

Both repos pin the same `proto` commit. When the protocol changes, `proto` is pushed
first, then both pins move together. A client and a relay on different `proto` commits
is exactly the desync the corpus exists to prevent.

Read `proto/pose_codec.md` before touching anything in `Net/Codec/`.

## Project structure

```
Main.cs / Main.*.cs        entry point + partials (Events, WorldActions, UiShots, DefaultHome)
Net/                      transport, API client, gateway client, WebRTC signalling, codec
  Codec/                  C# side of the wire format
    Tests/                golden tests (must match Rust) + transport liveness
Auth/                     OAuth PKCE, loopback listener on fixed port 34517
Avatar/                   humanoid rig, LOD tiers, retargeting, remote interpolation, copy
Player/                   LocalPlayer, RemoteAvatar, NameTag, Voice, VR rig (VrPlayer, IK, hands)
UI/                       Brand theme, chat, pause menu, avatar selector, touch controls, menus
Discord/                  Discord Social SDK: P/Invoke layer (cdiscord.h), client, rich presence
Events/                   ConcertRehearsal event world + diagnostics
World/                    interaction, held items, cinema speakers, house lights, hub render
Script/                   SerikaScript VM host bridge (OpCode, ScriptModule, ScriptVm, StringPool)
Shaders/                  concert effects (laser, haze, fire, smoke, sparkle, penlight, …)
Dialogue/                 dialogue manager addon
Assets/ Audio/            content
addons/                   third-party addons (dialogue manager, etc.)
bin/                      native libs bundled into the pck (Discord SDK, ffmpeg, yt-dlp)
ThirdPartyLicenses/       license notices for bundled binaries
proto/                    submodule → SerikaSocial/proto
```

## Features

- **Auth** — OAuth2 + PKCE via browser loopback (fixed port 34517), session JWTs,
  `serikasocial://` deep links.
- **Home** — single-player local world with a portal that joins The Commons.
- **The Commons** — multiplayer world via the Rust relay: poses, text chat, join/leave.
- **Avatars** — humanoid rigs from `.ska` files (default: Suisei), name tags, remote
  interpolation, in-world avatar selector, equip persists server-side.
- **VR (OpenXR)** — full-parity rig: head/hand IK, grip grabbing, teleport + smooth
  locomotion, snap/smooth turn, comfort vignette, haptics, controller-driven menus,
  optical hand tracking, arm-length scaling. Controls match VRChat by design.
- **Mobile** — touch controls (`UI/TouchControls.cs`), Quest + Mobile Android exports.
- **Events/Concerts** — `Events/ConcertRehearsal.cs` + concert shaders; live-event
  instances, crowd capture, performer effects, screen binding.
- **Discord Social SDK** — rich presence (world, party size, elapsed time, invite
  button) + Join-on-Discord (the join secret IS the `serikasocial://world/<id>` deep
  link). Desktop only. See the Discord section in `AGENTS.md` for the full constraint
  list (arRPC caveat, first-run consent, Public Client requirement).
- **SerikaScript** — sandboxed scripting VM for worlds (no reflection, no IO,
  whitelisted API, per-instance instruction budgets).

## Controls

### Desktop

WASD + mouse, Shift sprint, Ctrl crouch, Space jump, **V** first/third person,
**T** chat, **Esc** pause menu. The pause menu (`UI/PauseMenu.cs`) hosts world
actions: respawn, camera toggle, emotes, copy invite link, and Change avatar… .

### VR (VRChat-compatible bindings)

| Input | Action |
|---|---|
| Left stick | Locomote |
| Right stick | Turn (snap by default) |
| A (right) | Jump |
| X (left) | Mute |
| B / Y (either) | Quick menu (tap), action menu (hold 0.35 s) |
| Stick click | Action menu |
| Grip | Pick up |
| Trigger | Use / interact |
| Both menu buttons, held 1 s | Recentre |

`DeviceProfile.Settings.VrMoveOnRightStick` swaps the sticks; defaults to **false**
(movement on the left), matching VRChat.

## Testing & diagnostics

The codec golden tests must pass:

```bash
dotnet test Net/Codec/Tests    # C# golden tests (must match Rust) + transport liveness
```

Headless diagnostics (verify numerically, never by screenshot). Pass a real `.ska`
where shown — many avatars carry only the 22 body roles and no finger bones, so
finger checks report SKIPPED, which is not a pass. Prefer a VRM-sourced `.ska`
(`sourceFormat=vrm0`) for VR/finger tests.

```bash
$GODOT --headless --path game -- --serika-aimtest  --ska <path> [--clip Walk]   # head aim + nod/shake
$GODOT --headless --path game -- --serika-vrtest   --ska <path>                 # VrPlayer, no HMD needed
$GODOT --headless --path game -- --serika-fptest   --ska <path>                 # first-person eye probe
$GODOT --headless --path game -- --serika-animtest --clip <Clip> --ska <path>   # retargeting + crouch regression
$GODOT --headless --path game -- --serika-phystest --ska <path>                  # secondary physics
$GODOT --headless --path game -- --serika-keytest                              # keybinds: rebind, conflict, persist
$GODOT --headless --path game -- --serika-voicetest                            # voice: resample, pitch, VAD, codec
$GODOT --headless --path game -- --serika-discordtest [--wait 8]               # Discord SDK lifecycle
```

Rendered VR simulation (needs a display — `DISPLAY=:1` or `xvfb-run`):

```bash
$GODOT --path game --windowed --audio-driver Dummy -- --serika-vrsim --ska <path> --out /tmp/vr
```

`--serika-vrsim` registers real `XRServer` trackers under the real OpenXR names so
the shipping code path runs against a simulated OpenXR device. See `AGENTS.md` for
the full 44-assertion matrix and the mirror tests (`--serika-mirrortest`,
`--serika-mirrorworld`).

Headless smoke test against a live relay (needs a valid join ticket — see
[`tools`](https://github.com/SerikaSocial/tools) `mint-ticket.ts`):

```bash
$GODOT --headless -- --serika-smoke --endpoint <host:port> --ticket <token>
```

## Notes

- C# on Godot 4.x has **no web export**. Browser builds would need a second, non-C#
  client.
- The VRM importer ([V-Sekai `godot-vrm`](https://github.com/V-Sekai/godot-vrm)) is
  GDScript, so this project is mixed-language by necessity.
- Hot paths must stay allocation-free — `Variant` boxing on every `Set`/signal
  marshal puts the GC squarely in the frame-time path.
- The OpenXR startup modal is disabled at the source
  (`project.godot` sets `xr/openxr/startup_alert=false`). Do not re-enable it — a
  desktop player with no headset would otherwise get a blocking dialog every launch.

## Related repos

- [`server`](https://github.com/SerikaSocial/server) — REST API, gateway, Rust relay.
- [`proto`](https://github.com/SerikaSocial/proto) — the wire codec contract.
- [`godot-sdk`](https://github.com/SerikaSocial/godot-sdk) — creator tooling addon.
- [`docs`](https://github.com/SerikaSocial/docs) — architecture docs.
