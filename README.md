# Chaiser

Asset extractor for Unity games with custom archive formats, built as typed stage chains
(Elirium's parser chain idea). Mined from AnimeStudio, trimmed to what Enka and AIC need:
fast native asset maps, texture dumps, and Endfield building models.

Games: Arknights: Endfield (`--game ef`, default) and Honkai: Star Rail (`--game hsr`).

```
dotnet run -- maps                                   # build/print this client's maps
dotnet run -- dump-ui <outDir> <dir,dir,...> [--mirror] [--path <containerPathContains>]
dotnet run -- dump-ui --list [pathContains]           # texture counts per container dir
dotnet run -- dump-assets <assets.json> <outRoot>    # AIC's data-driven asset list
dotnet run -- extract-all                            # every building model -> output/models
dotnet run -- extract-prefab <container> <out.glb>   # a plain prefab's lod0 mesh (farm plots)
dotnet run -- --game hsr dump-ui <outDir> avatarroundicon/avatar --path spriteoutput
dotnet run -- crypto-selftest
```

Finding things no table names directly:

```
dotnet run -- search-containers <needle>              # distinct containers + asset type counts
dotnet run -- dump-cab <container>                    # every object in a prefab's file, scene graph trees
dotnet run -- dump-full-tree --container <container>  # unnamed MonoBehaviours of a prefab
dotnet run -- dump-mat <container> [--tex <prop> <out.png>]  # a Material's properties, a slot decoded
```

Global options: `--game ef|hsr`, `--client <data folder>`, `--maps <dir>`, `--rebuild-maps`.
Maps are a cache keyed by a fingerprint of the client's version/index files and built on first
use (~20 s EF, ~45 s HSR).

## Layout

- `Chain/`: the pipeline.
  - `Chain.cs`: stages, the runner, sessions and failures.
  - `Items.cs`: the typed items.
  - `Stages.cs`: the shared stages.
  - `BlockArchive.cs`: block table plus lazy node streams.
  - `GameProfile.cs`: per-game roots, fingerprint, archive format, `Dialect`.
  - `EncrFile.cs`: HSR's archive format.
- `VFSFile.cs`: Endfield's archive format.
- `MapStore.cs`: the map cache and the map-build chain.
- `UiDumper.cs`: the texture-dump chains.
- `Crypto/`: `Aes128` (shared core, pluggable S-box and key schedule), Endfield's VFS cipher,
  Mr0k.
- `Oodle.cs` + `native/AnimeStudio.Ooz.dll`: Oodle for HSR (vendored AnimeStudio build, x64).
- `BuildingExtractor.cs`, `GltfExporter.cs`, `tools/`: the Endfield building sprite pipeline
  (see HANDOFF.md).

Quirks key on the narrowest thing that explains them: the Unity version (from each file), then
`Dialect.Tech` (a vendor's engine changes), then `Dialect.Game`.
