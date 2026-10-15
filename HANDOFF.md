# Endfield Factory Sprite Pipeline — Handoff

Automated pipeline that extracts top-down building sprites for the community factory
planner directly from game data, replacing manual in-game screenshotting after each
update. Built as a standalone extractor (Chaiser, this repo) + Blender headless
renderer (`tools/`), independent of the full AnimeStudio GUI/CLI asset browser.

## Prerequisites (per game update)

1. **Game client updated** (including hotfixes) at `Z:\clients\endfield\EndField
   Game\Endfield_Data` (`GameProfile.Endfield.DefaultClient`, or `--client <Endfield_Data>`).
2. **Maps are automatic.** The extractor keys its AssetMap + CABMap on a fingerprint of the
   client's own resource indexes (`StreamingAssets` + `Persistent` `index_*.json`, which change
   with every update *and* hotfix) and builds them natively on first use into
   `maps/<fingerprint>.{assetmap,cabmap,json}` - about 30 s for the whole
   client (AnimeStudio.CLI took ~10 min single-threaded; output verified identical for
   Texture2D/Sprite/MonoBehaviour/Material/Mesh). Global options on any command:
   `--client <Endfield_Data>`, `--maps <dir>`, `--rebuild-maps`. `dotnet run -- maps` prints
   (and if needed builds) the current client's maps. No AnimeStudio.CLI step anymore.
3. *(was: CABMap rebuild)* - covered by 2; the CABMap is built over all of `Endfield_Data`, so
   hotfix bundles in `Persistent/VFS` resolve too.
4. **`FactoryBuildingTable.json`** (and its six router/valve/connector siblings —
   `FactoryGridRouterTable.json`, `FactoryBoxValveTable.json`,
   `FactoryGridConnecterTable.json`, `FactoryLiquidRouterTable.json`,
   `FactoryFluidValveTable.json`, `FactoryLiquidConnectorTable.json`) — currently expected
   under `C:\Projects\34_AIC\src\lib\data\`. These are the planner project's own game-data
   tables (port positions, footprint sizes, pedestal selection) and need to already be
   current for the new version — this pipeline reads them, it doesn't generate them.
5. **Blender** at `C:\Programs\blender-4.5.2-windows-x64\blender.exe` (or pass
   `-BlenderExe` to override).
6. **Python** with `PIL`/numpy available on PATH (used only if you resurrect any ad-hoc
   image inspection — the render pipeline itself no longer needs a Python post-process,
   see below).

If a default path above has moved: the client is `GameProfile.Endfield.DefaultClient` (or
`--client`), the rest are `Default...` declarations in `Program.cs` (`DefaultModelsDir` =
`output/models` in this repo, `DefaultFactoryBuildingTablePath`) and
the equivalent `param()` defaults in `tools/render_all_sprites.ps1`.

## Command sequence for a fresh update

```powershell
# 1. Clear stale intermediate models and sprites (extract-all skips files that already
#    exist on disk, so a stale .glb from a previous version won't get refreshed unless
#    you delete it first)
cd C:\Projects\15_EnkaNetwork\Chaiser
Remove-Item output\models\*.glb, output\sprites\*.png -Force

# 2. Extract every building's mesh+material(s)+ports into a combined .glb per building
#    (the client's maps build themselves on first use, ~30 s)
dotnet build   # if the extractor source changed since last run
dotnet run --no-build -- extract-all

# 3. Render every currently-present .glb to a top-down PNG sprite
.\tools\render_all_sprites.ps1
```

Step 3 output: `output/models/<building_id>.glb` (105/123 buildings
succeed; see "Expected failures" below).

Step 4 output: `output/sprites/item_port_<building_id>.png` — ready to
drop into the planner's `static/img/sprites/` folder.

**Timing**: extraction is fast (a couple minutes for all buildings). Rendering is the
slow part — roughly 30-80s per building through Cycles (128 samples + the emissive Glare
compositor pass), so the full batch takes on the order of 45-70 minutes. Both steps were
run via the `Monitor`/background-task pattern in this session to avoid blocking on a long
foreground wait — worth doing the same for a future run.

## Expected failures (not bugs)

The last clean run: **105 succeeded, 18 failed** out of 123 total AssetMap building
entries. All 18 failures are structural, not pipeline bugs — confirmed with the user
across this session:

- `region_102_bus_1_1` .. `region_102_bus_1_4` — grouping/base names for a depot-bus
  family; only meaningful as the family label, not individually placeable.
- `log_converger`, `log_splitter`, `log_connector`, `log_pipe_connector`,
  `log_pipe_converger`, `log_conditioner`, `log_pipe_conditioner`, `log_pipe_splitter` —
  bare/un-suffixed router-connector-valve base names. Their real content lives under
  numbered variants (`log_splitter_01`, `log_splitter_02`, etc.), which **do** extract
  successfully — this list is just the bare grouping key failing on its own.
- `travel_pole_1`, `travel_pole_2`, `marker_1` — real assets, but intentionally excluded
  from the planner's own building list (`build-buildings.ts` filters out `TravelPole`
  type and the literal id `marker_1`), so a missing sprite for these is correct, not a gap.
- `liquid_purifier_2`, `gas_reactor_1`, `vaporizer_1` — same "bare name has no mesh, only
  suffixed variants do" pattern as the log_* family above (e.g. `gas_reactor_1_01a` and
  `_01b` extract fine).

If a future run produces a **different** failure list (extra names beyond this set, or
fewer), that's worth investigating — it likely means the new game version restructured
something.

One separate, confirmed **not-implemented** case: `xiranite_oven_2` is excluded outright
from `extract-all` (see `KnownNiyBuildingIds` in `Program.cs`) — the user confirmed this
specific id has no real building behind it in-game at all, distinct from the "bare
grouping key" pattern above. If the game adds a real `xiranite_oven_2` later, remove it
from that set.

## Known open items / possible follow-ups

- **Port marker art is a placeholder pair** the user supplied
  (`assets/port_arrow_output.png` / `port_arrow_input.png` — orange
  chevron for output ports, desaturated/flipped for input ports). If the user provides
  updated art, just replace those two files at the same paths — no code change needed.
  Fluid/pipe ports (`isPipe: true` in the port data) are correctly excluded from getting
  a marker at all.
- **Emissive glow color is an approximation.** The real in-game corner-light color comes
  from sampling a per-material index into an undecoded `_EmissiveLut` gradient texture
  (117 entries observed on the pedestal material). This pipeline just hardcodes a
  matched-by-eye amber (`GltfExporter.cs`, `emissiveFactor`) instead of decoding the LUT.
  If different buildings turn out to have differently-colored accent lights in-game, this
  is where that would need real LUT decoding to fix properly.
- **Static port-direction marker mechanism was never found in game data.** An extensive
  search (AssetMap PPtr chains, ~900 texture names/images, the full
  `FactoryBuildingTable.json` family) never located the actual asset/mechanism the game
  uses to draw directional port chevrons — this is why the pipeline draws its own
  synthetic marker quads from table port-position data instead of extracting a real game
  asset. Not expected to block anything, just noting the investigation was inconclusive
  in case someone picks it up again later.
- `marker_1_01/02/03` — three separate `FactoryUnitConfigAsset`s that visually compose
  into one "Sign" prop in-game (confirmed by the user from renders: `_02` is a light-cone
  effect, `_03` a billboard target) but have **no discoverable data-side link** between
  them (not sibling nodes, not referenced by PathID from each other, not grouped by any
  table). Currently extracted/rendered as three independent sprites. Low priority per the
  user (`marker_1` is excluded from the planner's building list anyway).

## Farm plots (Soil)

Plot buildings (`soil_*`) resolve like any building, but `soil_1`'s FactoryUnitConfigAsset is a
deprecated planter box whose mesh is a 24-vertex placeholder at the origin (it renders empty).
The real bed is a plain prefab per planting template (`PlantingDataTable.modelTemplateId`):
`gameplay/factory/soilmodel/<template>_<state>.prefab`, `_0` dry and `_1` watered, each a
MeshFilter/MeshRenderer on `S_interactive_field+1_00x_01y_lod0` plus nine mount points for the
crops (`gameplay/factory/plantmodel/crop_*_postmodel`). `extract-prefab` reads these; the AIC
Workflow extracts `<template>_1` as `<template>_watered.glb` after `extract-all`.

## Surface maps

Materials carry `_NormalMap` plus either `_MROMap` (metallic R, roughness G, occlusion B) or, for
props without one, an NRO `_NormalMap` (normal XY in R/G, roughness B, occlusion A).
`ResolveSurfacePngs` turns both into a glTF normal map (Z rebuilt) and one ORM texture, so renders
pick up relief, metal sheen and wet glints. The `_MROMap` layout was checked by eye on
storager_1 only.

## Rendering pipeline internals (context for anyone touching `tools/`)

- `render_sprite.py` does the Blender-side work per model: import glTF, frame an
  orthographic top-down camera (from `FactoryBuildingTable.json`'s `range.width`/`depth`
  when known, else the mesh's own bounding box), set up flat two-sun lighting, and run the
  render through a small compositor graph: isolate Cycles' **Emit** pass → **Glare**
  (`FOG_GLOW`, `quality=HIGH`, `threshold=0`) → additive blend back onto the beauty image
  → alpha recombine → Composite. This gives the pedestal corner lights a real glow halo
  without a separate Python post-process (an earlier version of this pipeline did try a
  numpy-based bloom pass on the exported PNG — it's gone now; the native Blender
  compositor approach replaced it after research showed the original Glare attempt failed
  only due to using the tonemapped beauty image instead of the raw Emit pass, plus
  `quality=MEDIUM`'s internal downsample losing thin emissive geometry).
- Camera is rotated 180° around its own view axis (not a post-process image flip) to
  match the planner's expected orientation.
- `render_all_sprites.ps1` is the batch driver: enumerates every `.glb` in
  `output/models/`, looks up `tile_size` per building from the table, calls
  `render_sprite.py` once per model. It always re-renders every currently-present model
  on each run (not just newly-extracted ones) — this was an explicit choice so a
  lighting/framing/shader tweak applies uniformly across the whole set rather than
  leaving old sprites visually inconsistent with new ones. Extraction (step 3 above) is
  the one that diffs against existing output and skips redoing work.

## Useful debug commands (run from the repo root)

- `dotnet run -- maps` - fingerprint + map paths for the client (builds them if missing).
- `dotnet run -- maps-probe <file.chk>` - scan one bundle file like the map builder, with stack traces.
- `dotnet run -- maps-compare <other.assetmap>` - diff this client's map against another (e.g. AnimeStudio's).
- `dotnet run -- crypto-selftest` - VFS decrypt vs the original reference implementation.
- `dotnet run -- dump-ui --list [pathContains]` / `dump-ui <outDir> <dir,dir,...> [--mirror]` - bulk UI texture dump (Enka icons).
- `dotnet run -- find-in-assetmap <name>` - exact-name AssetMap lookup.
- `dotnet run -- search-names <substring>` — substring search across asset names/container
  paths (noisy; useful for broad exploration).
- `dotnet run -- search-and-dump-textures <containerPathSubstring> <name1,name2,...> [outDir]`
  — scoped texture search + bulk PNG dump, useful for hunting down a specific
  texture/icon by naming guesswork without eyeballing raw AssetMap text.
- `dotnet run -- list-buildings [mapPath]` / `list-missing [mapPath] [modelsDir]` — list
  or diff the full building set against what's already extracted.
- `dotnet run -- dump-material <assetName>` — deep field dump of one
  `FactoryUnitConfigAsset` (lodSettings, additiveMats, platformData, etc.).
- `dotnet run -- dump-body-material <assetName> [materialSlotIndex] [texPropertyName]` —
  dump a resolved body material's full property sheet (`m_TexEnvs`/`m_Floats`/`m_Colors`),
  optionally decoding+saving one named texture property (e.g. `_EmissiveMap`).
- `dotnet run -- dump-full-tree <assetName>` — raw JSON dump of an asset's entire type
  tree, for finding fields a targeted dump didn't think to print.
- `dotnet run -- <buildingId> [modelsOutDir]` (default/no subcommand) — extract one
  building verbosely, useful for iterating on a single problem building without
  re-running the whole batch.
