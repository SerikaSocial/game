# Bundled avatars (`.ska`)

There is **no bundled default avatar** — the default outfit is set by the web admin and served
from the API (`/v1/avatars/current`). The client downloads it to `user://avatars/current.ska`
at login. When no cloud default is available (offline, API down, or a blocked user), the client
falls back to a procedural "bean" character built from primitives (`AvatarInstance.CreateBean`),
so nobody is ever a capsule.

Downloaded avatars live under `user://avatars/<id>.ska` and are cached by `AvatarLibrary`.
