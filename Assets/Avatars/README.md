# Bundled avatars (`.ska`)

The client loads its default avatar from `res://Assets/Avatars/<name>.ska`. These `.ska` files are
**not committed** — they can be large and may carry third-party model licenses (e.g. the Suisei
VRM). Generate them locally before building:

```bash
python3 ../../tools/ska/vrm2ska.py /path/to/model.vrm Assets/Avatars/suisei.ska --name "…"
```

`AvatarLibrary.DefaultAvatarPath` points at `res://Assets/Avatars/suisei.ska`. If it's missing the
client falls back to the capsule stand-in (and logs a clear error), so a fresh clone still runs.
