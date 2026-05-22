# EndField Asset Index

## Default Workflow

Use the fast index by default. It builds the full VFS and string index, but does not deep-parse every logical `.ab` with AssetRipper.

```powershell
E:\RuriWorks\Ruri-RipperHook\.dotnet\dotnet.exe `
  E:\RuriWorks\Ruri-RipperHook\AssetRipper\Source\0Bins\AssetRipper\Release\Ruri.RipperHook.CLI.dll `
  --hook EndField_0.8.25 `
  --endfield-build-asset-index "F:\Hypergryph Launcher\games\Endfield Game" `
  --index-db E:\RuriWorks\Ruri-RipperHook\out\endfield_asset_index_fast.sqlite
```

The fast index writes:

- `vfs_files`: logical file path, chunk path, offsets, lengths, VFS metadata.
- `strings`: useful strings extracted from logical `.ab` payloads.
- `index_jobs`: per logical `.ab` status. Fast indexed jobs use `lightweight`.

On the current release client, expected first-pass scale is roughly:

- `vfs_files=282087`
- logical `.ab=245606`
- `strings` near one million, depending on string filters.

## Query Workflow

Query the SQLite index first. The query path automatically applies the `EndField_0.8.25` hook if no hook is explicitly provided.

```powershell
E:\RuriWorks\Ruri-RipperHook\.dotnet\dotnet.exe `
  E:\RuriWorks\Ruri-RipperHook\AssetRipper\Source\0Bins\AssetRipper\Release\Ruri.RipperHook.CLI.dll `
  --endfield-query-index E:\RuriWorks\Ruri-RipperHook\out\endfield_asset_index_fast.sqlite `
  --query zhuangfangyi `
  --target-types Material Texture2D Mesh Shader `
  --report-out E:\RuriWorks\Ruri-RipperHook\out\zhuangfangyi_index_report_fast.json
```

Query behavior:

- First reads `assets`, `strings`, and `vfs_files` from SQLite.
- If the query only has string/VFS hits, it deep-parses up to 32 matching logical `.ab` files.
- Parsed `assets`, `asset_references`, and `cab_dependencies` are written back to the same DB.
- Later queries reuse those deep-parsed records.

For the `zhuangfangyi` validation, the fast DB query deep-parsed 16 packages and returned Material/Mesh candidates, including:

- `M_fxmap_zhuangfangyi_ceiling_01`
- `M_fxmap_zhuangfangyi_shu_03_04`
- `S_fx_zhuangfangyi_shu2_mb_uninter`
- `S_fx_zhuangfangyi_shu3_uninter`
- `S_fx_zhuangfangyi_zhuozi2_7`

## Full Deep Index

Use `--deep-asset-index` only when a long offline full parse is explicitly needed.

```powershell
E:\RuriWorks\Ruri-RipperHook\.dotnet\dotnet.exe `
  E:\RuriWorks\Ruri-RipperHook\AssetRipper\Source\0Bins\AssetRipper\Release\Ruri.RipperHook.CLI.dll `
  --hook EndField_0.8.25 `
  --endfield-build-asset-index "F:\Hypergryph Launcher\games\Endfield Game" `
  --index-db E:\RuriWorks\Ruri-RipperHook\out\endfield_asset_index_deep.sqlite `
  --deep-asset-index
```

Deep index behavior:

- Attempts AssetRipper parsing for every logical `.ab`.
- Populates `assets`, `asset_references`, and `cab_dependencies` during the build.
- Queries are faster after completion because more relationships are already indexed.
- Build time and risk are much higher; failures are isolated per job in `index_jobs`.

Current limitation: `GameFileLoader` is process-global, so the first version records requested `--parallel` in the manifest but deep parsing is kept serial in-process.

## Operational Notes

- Do not use the old full deep parse as the default daily lookup flow.
- Use fast index first, then let query-time deep parsing grow the DB around the assets being investigated.
- If a query returns `Status=ok` but `CandidateCount=0`, inspect `StringHits`; this means the DB has string evidence but no parsed asset candidates yet.
- Query-time deep parsing must run with the EndField hook active. The CLI now defaults query indexing to `EndField_0.8.25` when no hook is supplied.
- Job outputs are written under `<index-name>.index-run/outputs`; logs under `<index-name>.index-run/logs`.
- SQLite remains parent-owned; worker/job output is JSONL and imported after validation.

## Report Layout

Do not write EndField investigation reports into the repository root. Put human-facing JSON/TXT/MD reports under `Report/<Category>/`, named by the asset or topic being investigated.

Current categories:

- `Report/DeferredLighting/`
- `Report/Foliage/`
- `Report/Grass/`

Examples:

- `Report/DeferredLighting/EndField_DeferredLighting_metadata.json`
- `Report/Foliage/EndField_Foliage_all_paths.json`
- `Report/Grass/EndField_M_grass_tygrass_2_003_01_metadata.json`

This rule applies to manually generated reports and locator/metadata outputs. It does not apply to SQLite index DBs, `.index-run` job files, or transient export/test output under `out/`.
