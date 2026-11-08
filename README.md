# CBRE Digital Twin: Construction & Listing Intelligence

**HackUTD 2026, CBRE challenge.** This is an end-to-end working prototype. It joins fragmented real-estate data (plans, building-system ages, IoT-style telemetry, hazard maps and market comps) into one live digital twin. The twin **predicts cascading operational failures**, **quantifies business impact** and **executes fixes before disruptions spread**. A Unity 3D client generates a walkable prototype from each blueprint. A team of **NVIDIA Nemotron** agents analyses, prices and acts, and runs *inside* **SpacetimeDB**.

```
┌──────────── Unity 6 client (URP + UI Toolkit) ─────────────┐
│ Login → Agent workspace → pick example blueprint            │
│ • Procedural 3D prototype (walls/doors/windows/furniture)   │
│ • Structural heatmap · system risk pills · cascade sparks   │
│ • Orbit / first-person Walk / 2D Blueprint view             │
│ • Live teammates + shared pins (multi-user walkthrough)     │
└───────────────▲─────────────────────────┬───────────────────┘
   subscriptions│(every row, live)        │reducers / procedure
┌───────────────┴─────────────────────────▼───────────────────┐
│ SpacetimeDB module `cbre-twin` (TypeScript, V8)              │
│ Tables  ─ location · blueprint · room · component · rules    │
│ Engines ─ IRC structural checks · Weibull + noisy-OR cascade │
│           · location-aware valuation · feature demand · ROI  │
│ Scheduled reducer ─ monthly wear, NOAA hail, freeze, sensors │
│ Procedure `ask_agent` ─ Nemotron orchestrator loop (HTTP)    │
│   ├ consult_structural_agent  (Nemotron specialist)          │
│   ├ consult_operations_agent  (Nemotron specialist)          │
│   ├ consult_market_agent      (Nemotron specialist)          │
│   ├ simulate_fix              (what-if, in-transaction)      │
│   └ dispatch_work_order       (executes a fix, ACID write)   │
│ Env secret ─ NVIDIA_API_KEY (never reaches a client)         │
└──────────────────────────────────────────────────────────────┘
```

## How this uses SpacetimeDB's unique features

| SpacetimeDB feature | How the twin uses it |
|---|---|
| **The database is the server**: logic runs in the module | There is no API server. All analytics (structural rules, cascade model, valuation) run in reducers next to the data. |
| **Transactional reducers** | A work order (`createWorkOrder` / `advanceWorkOrder`) updates the component and recomputes the risk graph, price and alerts in one atomic transaction. |
| **Procedures with outbound HTTP** (`ctx.http.fetch`, `ctx.withTx`) | The whole **Nemotron agent loop runs inside the database**. Tool calls read and write the same tables in short transactions. |
| **Scheduled reducers** (`sim_timer`, `ScheduleAt.interval`) | A live simulation advances months of wear and weather (NOAA hail, freeze) and drifts sensors, which trips alerts. |
| **Real-time subscriptions** | Every agent step (`agent_step`), alert, work order and teammate pose streams to every client. The agent's reasoning trace appears as it happens. |
| **Identity + per-user view** | Login creates a SpacetimeDB identity (token kept per server). `my_profile` is a per-sender view. |
| **Module environment secrets** | `NVIDIA_API_KEY`, `NEMOTRON_MODEL` and `NEMOTRON_SPECIALIST_MODEL` are declared env values, set at publish time. |
| **Generated, typed C# bindings** | `spacetime generate --lang csharp` writes `Assets/Scripts/SpacetimeBindings`. Unity calls `conn.Reducers.*` and `conn.Procedures.AskAgent(...)`. |
| **Hot-swap publish** | You can republish the module while clients stay connected. |

## The models (all in `SpacetimeServer/spacetimedb/src/engine.ts`)

1. **Structural integrity.** The model checks spans against AWC/IRC joist and rafter span tables (it detects whether the level above is carried). It also flags removed bearing walls, per-level wall stacking (load-path continuity, IRC R502.4), cantilevers (R502.3.3 / R507.6), glazing on braced walls (R602.10), egress (R310), tall walls (R602.3) and foundations on expansive clay (R403.1.8, USDA shrink-swell). It also checks construction sequencing for the new build. Output is an integrity score from 0 to 100 plus findings with fix costs.
2. **Failure prediction.** Each system gets a Weibull hazard (k = 3, mean = NAHB/InterNACHI service life). The hazard is adjusted for condition, location (hail, soil, freeze, humidity, flood) and **live sensor readings past their warn/critical thresholds**. The result then propagates through a **noisy-OR failure graph**, for example *gutters → foundation → plumbing → mold* or *roof → insulation → HVAC → mold*. The output is the 12-month probability (alone vs. with cascade), the expected loss, and **prevention value**: the dollars the whole graph avoids if you fix that one system now.
3. **Business impact.** Price = sqft × local $/sqft, adjusted for age, layout, demand-weighted features, inspection credits, structural stigma and cascade-risk drag. The model also produces a range, confidence, days on market, annual carry cost and the *value unlocked* by pre-listing repairs. **The same model is priced in 6 markets** (Richardson, Plano, Uptown Dallas, Frisco, Austin, Houston) with each market's own hazards.
4. **Selling points.** Feature premiums are weighted by each location's buyer tags. Upgrades use Cost vs. Value 2024 recoup rates and are ranked by ROI, together with repairs that prevent cascades.

Data sources are listed in the app (**Data & research** tab) and in `seed.ts`. Seed values are **approximations compiled from public research** for demo purposes: IRC 2021, AWC span tables, FEMA P-58, NAHB life expectancy, USDA NRCS, NOAA SPC, FEMA NFHL, CPSC (Federal Pacific panels), III claims data, Zillow/Redfin, Zonda Cost vs. Value and NAR. Replace them with live feeds for production use.

## Example blueprints

| # | Blueprint | Story |
|---|---|---|
| 1 | Canyon Creek Ranch (Richardson, 1979) | Hidden deferred maintenance: FPE panel, 17-yr roof, single-pane windows, slab cracking on Houston Black clay |
| 2 | Willow Bend Two-Story (Plano, 2006) | A bearing wall was removed under the bedrooms, and there is a 4 ft cantilevered primary suite |
| 3 | State-Thomas Townhome (Uptown Dallas, 2015) | A third-floor water heater past its life sits above two finished floors, plus an uninspected roof-deck membrane |
| 4 | Fields Modern Farmhouse (Frisco, 2026, under construction) | Construction-manager view: dry-in before hail season, an 18 ft glass wall, a glulam patio and framing moisture |

## Run it

### 1. SpacetimeDB (local)

```bash
spacetime start
```

```bash
cd SpacetimeServer && (cd spacetimedb && npm install) && spacetime publish --yes
```

`spacetime.json` targets the `local` server and the database `cbre-twin`. To reset all data, add `--delete-data=always`.

### 2. Turn on Nemotron agents

Get a key at <https://build.nvidia.com> (Settings → API Keys). Then publish it into the database environment:

```bash
cd SpacetimeServer && NVIDIA_API_KEY=nvapi-XXXX spacetime publish --yes
```

Optional overrides (the defaults are below):

```bash
NEMOTRON_MODEL=nvidia/nemotron-3-super-120b-a12b NEMOTRON_SPECIALIST_MODEL=nvidia/nemotron-3-super-120b-a12b spacetime publish --yes
```

Without a key, the same agent graph runs with deterministic specialists, so the demo never breaks. The trace says which mode ran.

### 3. Maincloud (HackUTD credits)

1. Run `spacetime login`. Then redeem the code **`HackUTD2026`** in your SpacetimeDB account on spacetimedb.com.
2. Publish under a globally unique name:

```bash
cd SpacetimeServer && NVIDIA_API_KEY=nvapi-XXXX spacetime publish cbre-twin-<team> --server maincloud --yes
```

3. In Unity's login screen, open **Server settings**. Set the server to `https://maincloud.spacetimedb.com` and the database to `cbre-twin-<team>`.

### 4. Unity

1. Open the project in **Unity 6000.6.8f1**. Package Manager pulls `com.clockworklabs.spacetimedbsdk@v2.14.1` from git (`Packages/manifest.json`), so git must be on your PATH.
2. Open `Assets/Scenes/SampleScene.unity` and press **Play**. Everything (camera, lighting, UI, connection) is created at runtime by `TwinBootstrap`.
3. The sign-in screen shows a live model turning behind the form. Enter your name, pick **Construction manager** or **Real estate agent**, then click **Continue**.

### Walkthrough mode

Choose **Walkthrough** in the top bar (or **Walk in** on any room). All four example homes have fully connected routes on every floor for a first-person, VR-style tour of the generated house:

- You are at eye height (1.62 m) inside a collision capsule. Walls and glass block you, and doorways let you through.
- A guided route visits every main room level by level along real doorways, with a short narration per room drawn from the twin's data. Rooms with critical findings are marked on the progress bar.
- VR comfort features: 30° snap turns, fade-teleport to the floor under the reticle, and a vignette that narrows the view while you move.
- Gaze inspection: look at a building system to see its live reading, and click to open its risk card or schedule a repair.
- **Ask about this room** sends the current room to the Nemotron advisor, and the answer appears in the walkthrough.

| Key | Action |
|---|---|
| W A S D (Shift to hurry) | Walk |
| Mouse (click to capture, Esc to release) | Look |
| Q / E | Snap turn 30° |
| T | Teleport to where you're looking |
| N / P | Next / previous room |
| Space | Auto tour |
| Click | Inspect what's under the reticle |
| X | Leave the walkthrough |

On a Meta Quest the same walkthrough runs natively in the headset (see below).

After you change the module schema, regenerate the bindings:

```bash
cd SpacetimeServer && spacetime generate --lang csharp --out-dir ../Assets/Scripts/SpacetimeBindings --module-path spacetimedb --yes
```

## Meta Quest

The same project runs as a Quest app. When a headset is active at launch, `TwinBootstrap` skips the desktop UI and starts `QuestApp`, which uses the **Meta XR Core SDK** (`OVRCameraRig`, `OVRInput`), the **Oculus XR Plugin** and the **Meta Voice SDK**.

### Build it

1. In Unity Hub, add **Android Build Support** (with OpenJDK and Android SDK/NDK) to Unity 6000.6.8f1.
2. Turn on developer mode on the Quest (Meta Horizon phone app → Devices → Developer Mode) and connect it by USB-C.
3. In Unity: **Keystone → Quest → 1. Configure project for Meta Quest**. This switches to Android and sets IL2CPP ARM64, Vulkan and the Oculus XR loader.
4. Point the headset at your database, either:
   - **Keystone → Quest → 2. Point headset at this Mac (Wi-Fi)**, then run the server so the headset can reach it:

     ```bash
     spacetime start --listen-addr 0.0.0.0:3000
     ```

   - or **2b. Point headset at Maincloud**, after publishing there.
5. To enable voice, save your Wit.ai token in the desktop app first (Ask the agent → Voice). The config step copies it into `Assets/Resources/keystone_config.json`.
6. **Keystone → Quest → 3. Build and run on headset**.

### In the headset

- **Home picker:** a floating panel. Point and pull the right trigger, or say a home's name.
- **Walking around:** the house is built around you at full scale, with ceilings. Walls block you and doorways let you through.

| Control | Action |
|---|---|
| Left stick | Walk |
| Right stick ← → | Snap turn 30° |
| Right trigger | Click what you point at: panel buttons, a building system (opens its risk card with **Fix it** and **Ask**), or the floor (teleport) |
| A / X | Next / previous room (fade teleport) |
| B | Ask the agent about this room (the answer floats beside you) |
| Hold left trigger | Talk: notes, "next room", "fix the roof", or questions |
| Y / Menu | Back to the home picker |

- **Wrist menu:** shows the room name with Next / Back / Ask / Homes buttons.
- **Room cards:** a card appears at each room with its narration.
- **Shared with desktop:** notes appear as pins, and teammates on desktop or another headset show up live.

`Keystone.MetaXR` (the only code that touches Meta XR types) compiles only when `com.meta.xr.sdk.core` is installed, so the desktop app doesn't depend on it.

## Voice (Meta Voice SDK)

Voice uses Meta's **Voice SDK** (`com.meta.xr.sdk.voice` 85.0.1, Wit.ai dictation), installed from Meta's registry (`https://npm.developer.oculus.com`, declared in `Packages/manifest.json`). It works in the editor and in desktop players, and the same code runs on Meta Quest.

1. Create a free app at <https://wit.ai> and copy its **Client Access Token** (Settings → Tokens).
2. In the app, open **Ask the agent → Voice**, paste the token and click **Save**. Alternatively, set `WIT_CLIENT_TOKEN` in the environment before launching.
3. Hold **V** anywhere, or tap **MIC**, and speak:

| You say | What happens |
|---|---|
| Anything else ("water stain on the ceiling by the vent") | Saved as a note, pinned in the room you're in and shared live with everyone |
| "next room" / "go back" / "exit tour" | Walkthrough navigation |
| "start the tour" | Opens the walkthrough |
| "fix the roof" / "book a repair for the water heater" | Creates a work order |
| A question ("what will fail first?", "ask what should this list for") | Sent to the Nemotron agent |

The agents read the latest notes, so spoken on-site observations feed their answers. The Meta integration is isolated in `Assets/Scripts/MetaVoice/`, an assembly that only compiles when the package is present. The rest of the app talks to `VoiceHub` in `Assets/Scripts/VoiceCore/`.

## Ask the agent

The **Ask the agent** tab lets you pick a topic (Structure, What breaks, Price, Selling points, Build plan, A room, A system, Our notes). Then you can pick a specific room or system and tap a suggested question, or type or speak your own. The agents' steps appear live on the right.

## Demo script (about 3 minutes)

1. **Log in** as a Real Estate Agent and open *Canyon Creek Ranch*. The house builds itself from database rows.
2. In **Failure forecast**, *Drainage → Foundation → Plumbing → Mold* lights up as a red spark arcing through the house.
3. Ask the agent **“Fix the top risk now”**. Then switch to **Agent trace (live)**. You see the orchestrator hand off to the three specialists, run `simulate_fix`, then **execute `dispatch_work_order`**.
4. Click **Simulate time**. Months tick, a NOAA hail alert fires, sensors drift and the work order completes. The cascade probability, price and integrity scores update live.
5. Open **Pricing & location** to see the price bridge and the same house in 6 markets.
6. Open a second Unity instance (or a teammate's) on the same database. The teammate's avatar and pins appear in your walkthrough.
7. Switch to Construction manager on *Fields Modern Farmhouse*. Open **Walk through**, press **Auto tour**, look at the dry-in and framing markers, and click **Ask about this room**.

## Project layout

```
SpacetimeServer/spacetimedb/src/
  schema.ts   tables, scheduled table, env declarations
  seed.ts     locations, blueprints, systems, cascade graph, research sources
  engine.ts   structural · Weibull/noisy-OR risk · valuation · features · ROI
  ops.ts      work-order execution
  agent.ts    Nemotron orchestrator + specialists (procedure)
  index.ts    lifecycle, reducers, simulation, view
Assets/Scripts/
  SpacetimeBindings/   generated C# client bindings
  Twin/TwinBootstrap.cs   zero-setup runtime entry point
  Twin/TwinConnection.cs  SpacetimeDB connection + cache queries
  Twin/BlueprintBuilder.cs procedural 3D prototype + overlays
  Twin/CameraRig.cs        orbit / plan / immersive walkthrough
  Twin/TourPlanner.cs      doorway-routed guided tour + narration
  Twin/PresenceSync.cs     multi-user presence
  Twin/TwinUI.cs           sign-in, Overview / Walkthrough / Agent pages, voice handling
  VoiceCore/VoiceHub.cs    voice abstraction used by the UI
  MetaVoice/MetaVoiceBackend.cs  Meta Voice SDK (Wit.ai dictation) backend
Assets/Resources/UI/   Twin.uss (light theme), TwinTheme.tss
```

## Known limitations

- Materials use `Shader.Find("Universal Render Pipeline/Lit")`. That works in the editor. For a player build, add a material that uses URP/Lit (and Sprites/Default) to the project so the shader variants are included.
- Seed market and hazard values are research-informed approximations, not live feeds.
- Authentication is SpacetimeDB's built-in anonymous identity plus a profile. Swap in SpacetimeAuth/OIDC for production.
