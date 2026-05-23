# Endfield Export Workflow

Last updated: 2026-05-23

This document is the handoff runbook for the current Endfield-focused Unity asset export flow from the repository root. It describes the normal indexing, dependency resolution, export, and validation path only. Do not add literal key material or private implementation details to this document.

## Current Scope

The accepted workflow is a focused character export for Zhuang Fangyi:

- Seed names: `zhuangfy`, `chr_0030_zhuangfy`, `actor_zhuang`, `m_actor_zhuang`, `fangyi`.
- Target closure: direct character assets plus reachable Material, Texture2D, Shader, and supporting CAB dependencies.
- Non-goal: full-game export.
- Local game data root: set `END_FIELD_GAME_ROOT` to the Endfield install directory.
- Local output root: `TestLoopOutput\EndfieldZhuangFullRun`.
- Hook id: `EndField_0.8.25`; CLI accepts `--hook EndField_0_8_25`.

## Code Map

- `Source/Ruri.RipperHook.CLI/Cli/CliOptions.cs`: command-line options.
- `Source/Ruri.RipperHook.CLI/Cli/VfsCliRunner.cs`: VFS index, metadata probe, term scan, dependency closure, logical bundle materialization, export, and material report flow.
- `Source/Ruri.RipperHook.CLI/Cli/HeadlessRunner.cs`: headless AssetRipper load/export path.
- `Source/Ruri.RipperHook.CLI/Cli/CabMap.cs`: older direct CABMap dependency helper.
- `Source/Ruri.RipperHook/AssetRipperGameHook/UnityHypergryph/Endfield/`: Endfield version hooks and VFS format adapters.
- `Source/Ruri.RipperHook/AssetRipperHook/ShaderDecompiler/`: shader export and HLSL decompile support.
- `skills/endfield-asset-recovery/SKILL.md`: local agent checklist for this workflow.

## Data Flow

1. Build the CLI and enable the Endfield hook.
2. Build a SQLite VFS index from `.blc` metadata and referenced `.chk` payloads.
3. Probe logical `.ab` payload metadata and record CAB names plus dependency edges.
4. Scan indexed payloads for target seed terms.
5. Resolve the CAB dependency closure from selected logical `.ab` paths or CAB names.
6. Materialize only the selected logical bundle closure under `TestLoopOutput`.
7. Export through the normal headless AssetRipper project exporter.
8. Repair/inspect Unity material shader links using the recorded VFS dependency map.
9. Open the exported project with Unity batchmode and check dependency reports.

## Standard Commands

Set common variables first:

```powershell
$repo = (Get-Location).Path
$gameRoot = $env:END_FIELD_GAME_ROOT
if ([string]::IsNullOrWhiteSpace($gameRoot)) { throw "Set END_FIELD_GAME_ROOT to the Endfield install directory." }
$out = Join-Path $repo "TestLoopOutput\EndfieldZhuangFullRun"
$db = Join-Path $out "endfield_vfs.sqlite"
$cli = Join-Path $repo "Source\Ruri.RipperHook.CLI\bin\Release\net10.0-windows\Ruri.RipperHook.CLI.exe"
```

Kill stale CLI runs before starting a new long run:

```powershell
Get-Process Ruri.RipperHook.CLI -ErrorAction SilentlyContinue | Stop-Process -Force
```

Build and verify hook discovery:

```powershell
dotnet build "$repo\Ruri-RipperHook.slnx" -c Release

& $cli --list-hooks
```

The hook list should include `EndField_0.8.25`.

Build or refresh the VFS index:

```powershell
& $cli `
  --hook EndField_0_8_25 `
  --game-root $gameRoot `
  --output-root $out `
  --vfs-db $db `
  --build-vfs-index
```

Scan for the current character terms:

```powershell
& $cli `
  --hook EndField_0_8_25 `
  --output-root $out `
  --vfs-db $db `
  --scan-vfs-terms "zhuangfy,chr_0030_zhuangfy,actor_zhuang,m_actor_zhuang,fangyi" `
  --closure-out (Join-Path $out "zhuangfy_term_hits.json")
```

Probe metadata. A targeted pass is useful after term scanning:

```powershell
& $cli `
  --hook EndField_0_8_25 `
  --output-root $out `
  --vfs-db $db `
  --probe-vfs-metadata `
  --probe-vfs-hit-metadata `
  --resume
```

If dependency closure still reports unresolved CABs, continue probing the full indexed `.ab` set. Use `--resume` and `--shard i/n` for long runs:

```powershell
& $cli `
  --hook EndField_0_8_25 `
  --output-root $out `
  --vfs-db $db `
  --probe-vfs-metadata `
  --resume `
  --shard 0/4
```

Resolve a dependency closure from a selected logical `.ab` path or CAB seed:

```powershell
& $cli `
  --hook EndField_0_8_25 `
  --output-root $out `
  --vfs-db $db `
  --vfs-deps "<logical .ab path or CAB seed>" `
  --closure-out (Join-Path $out "zhuangfy_closure.json")
```

Materialize and export selected seeds. Multiple seeds can be separated by `;` or `,`:

```powershell
& $cli `
  --hook EndField_0_8_25 `
  --output-root $out `
  --vfs-db $db `
  --load-logical "<seed1.ab;seed2.ab>" `
  --resolve-vfs-deps `
  --export (Join-Path $out "ExportedProject")
```

Inspect and repair exported Unity material shader links:

```powershell
$project = Join-Path $out "ExportedProject\ExportedProject"

& $cli `
  --hook EndField_0_8_25 `
  --output-root $out `
  --vfs-db $db `
  --repair-unity-materials $project `
  --repair-report (Join-Path $out "Reports")
```

Unity batchmode import check:

```powershell
$unity = $env:UNITY_2021_3_34F1_EDITOR
if ([string]::IsNullOrWhiteSpace($unity)) { throw "Set UNITY_2021_3_34F1_EDITOR to the Unity editor executable." }

& $unity `
  -batchmode `
  -nographics `
  -quit `
  -projectPath $project `
  -logFile (Join-Path $out "unity_import.log")
```

## Important Outputs

- `endfield_vfs.sqlite`: VFS logical file, payload, CAB metadata, and dependency-edge database.
- `vfs_scan_summary.md`: index summary.
- `zhuangfy_term_hits.json`: seed term scan output.
- `zhuangfy_*_closure*.json`: selected dependency closure snapshots.
- `vfs_load_manifest.json`: materialized logical bundle manifest for the latest export run.
- `materialized/`: temporary selected logical bundles.
- `ExportedProject/ExportedProject`: generated Unity project.
- `Reports/zhuangfy_unresolved_dependencies.json` and `.md`: material dependency validation reports.

Do not stage `TestLoopOutput/`; it is local run output.

## Acceptance Snapshot

Current accepted project path:

```text
TestLoopOutput\EndfieldZhuangFullRun\ExportedProject\ExportedProject
```

Accepted counts for `Assets/`:

- `.mat`: `16`
- `.png`: `39`
- `.shader`: `6`
- `.hlsl`: `2810`
- Shader deadbeef refs: `0`
- Texture deadbeef refs: `0`
- Unresolved refs: `0`
- Fallback shader used: `false`
- Unity `2021.3.34f1` batchmode open: exit code `0`

Unity may create extra package-cache icon PNG files under `Library/PackageCache`; do not count those as exported texture assets.

## Validation Rules

- A `.mat` is accepted only when resolvable shader and texture references are preserved or reported.
- Fallback shaders are diagnostic only and do not count as success.
- Remaining deadbeef GUID references are real dependency gaps.
- If `--vfs-deps` returns `partial`, continue metadata probing before accepting the export.
- If material repair is `partial` or `error`, inspect `zhuangfy_unresolved_dependencies.json` and `zhuangfy_shader_probe.json`.
- Keep implementation changes in `Source/Ruri.*/**`; do not patch upstream submodules for the final solution.

## Quick Troubleshooting

- `--list-hooks` does not show `EndField_0.8.25`: rebuild the solution and verify the hook project is referenced by the CLI.
- `--load-logical` resolves no payloads: check whether the seed is an exact logical path suffix, a CAB name, or a semicolon/comma-separated seed list.
- Closure has unresolved CABs: run more `--probe-vfs-metadata --resume` passes, optionally sharded.
- Material report has deadbeef refs: do not replace them with fallback; treat them as missing dependency evidence.
- Unity import creates extra local cache files: keep validation focused on `Assets/` and reports.
