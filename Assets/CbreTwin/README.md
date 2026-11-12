# CBRE Digital Twin (add-on)

A self-contained desktop and Meta Quest client for the CBRE building twin, backed by the
SpacetimeDB module in `SpacetimeServer/`. It lives next to the main Cascade app and does
not change it.

## Isolation from the rest of the project

- **Own folder and assembly.** Everything is under `Assets/CbreTwin/`. The scripts compile
  into the `CbreTwin` assembly (`autoReferenced: false`), so its generated
  `SpacetimeDB.Types` bindings never clash with the Atlas or Cascade bindings in
  `Assembly-CSharp`.
- **Own scene only.** `TwinBootstrap` starts the app only when the active scene is
  `Assets/CbreTwin/Scenes/CbreTwin.unity`. Every other scene is untouched.
- **No project settings or packages changed.** The Meta XR and Meta Voice integrations
  (`Scripts/MetaXR`, `Scripts/MetaVoice`) only compile when `com.meta.xr.sdk.core` and
  `com.meta.xr.sdk.voice` are installed. Without them, the twin runs in desktop mode.

## Run it

1. Open `Assets/CbreTwin/Scenes/CbreTwin.unity` and press Play.
2. The client connects to the database named in `Resources/keystone_config.json`
   (default `hackutd-g14me` on Maincloud).

## Backend

```sh
cd SpacetimeServer/spacetimedb
npm install
cd ..
spacetime publish --server maincloud
```

After changing the module, regenerate the C# bindings:

```sh
cd SpacetimeServer
spacetime generate --lang csharp --module-path spacetimedb --out-dir ../Assets/CbreTwin/Scripts/SpacetimeBindings
```

## Known limitations in this project

- The twin's materials were authored for URP, and this project uses the built-in pipeline,
  so the twin falls back to the `Standard` shader. Transparent surfaces may look opaque.
- The Quest build tooling from the original branch (project configuration and headless APK
  build) is not included, because it rewrites player and XR settings this project manages
  itself.
