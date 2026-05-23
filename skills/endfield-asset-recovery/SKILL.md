---
name: endfield-asset-recovery
description: Use when working inside the AtCat-RipperHook repository on the current Endfield-focused Unity asset export flow, including VFS/CAB indexing, logical .ab loading, material dependency closure, shader export validation, and Zhuang Fangyi asset acceptance.
---

# Endfield Asset Recovery

## Scope

- Repo: current repository root
- Game data root: set `END_FIELD_GAME_ROOT` to the Endfield install directory
- Build target: `Ruri-RipperHook.slnx`
- Runbook: `docs\endfield-export-workflow.md`
- Local output root: `TestLoopOutput`
- Current focused run: `TestLoopOutput\EndfieldZhuangFullRun`
- Current acceptance target: Zhuang Fangyi / `zhuangfy` / `chr_0030_zhuangfy` Material, Texture2D, and Shader closure.

This skill covers the AtCat-RipperHook implementation and validation workflow only. Treat older repo paths as stale unless the user explicitly asks.

## Guardrails

- Do not edit `AssetRipper/**` or other submodules unless the user explicitly asks.
- Do not revert user changes in `Source/Ruri.SourceGenerated/Ruri.SourceGenerated.csproj`.
- Do not stage or commit `TestLoopOutput/`; it is local run output.
- Do not print literal key material in docs, logs, reports, or final summaries.
- Do not treat fallback shader output as success. Fallback is diagnostic only.
- Do not hide unresolved shader or texture refs. Write them to the unresolved dependency report.
- Keep game-specific behavior in Ruri.Hook attribute hooks and CLI workflow code, not upstream submodule patches.

## Important Paths

- Hook code: `Source/Ruri.RipperHook/AssetRipperGameHook/UnityHypergryph/Endfield/`
- Shared shader export hook path: `Source/Ruri.RipperHook/AssetRipperHook/ShaderDecompiler/`
- VFS/CAB CLI flow: `Source/Ruri.RipperHook.CLI/Cli/VfsCliRunner.cs`
- CLI options: `Source/Ruri.RipperHook.CLI/Cli/CliOptions.cs`
- Handoff runbook: `docs/endfield-export-workflow.md`
- Current exported Unity project: `TestLoopOutput/EndfieldZhuangFullRun/ExportedProject/ExportedProject`
- Dependency report: `TestLoopOutput/EndfieldZhuangFullRun/Reports/zhuangfy_unresolved_dependencies.json`

## Standard Commands

Build:

```powershell
dotnet build .\Ruri-RipperHook.slnx -c Release
```

List hooks:

```powershell
& .\Source\Ruri.RipperHook.CLI\bin\Release\net10.0-windows\Ruri.RipperHook.CLI.exe `
  --list-hooks
```

Expected hook facts:

- hook list contains `EndField_0.8.25`
- `--hook EndField_0_8_25` is accepted through normalization

Build or refresh the VFS index:

```powershell
$repo = (Get-Location).Path
$gameRoot = $env:END_FIELD_GAME_ROOT
if ([string]::IsNullOrWhiteSpace($gameRoot)) { throw "Set END_FIELD_GAME_ROOT to the Endfield install directory." }
$out = Join-Path $repo "TestLoopOutput\EndfieldZhuangFullRun"
$db = Join-Path $out "endfield_vfs.sqlite"
$cli = Join-Path $repo "Source\Ruri.RipperHook.CLI\bin\Release\net10.0-windows\Ruri.RipperHook.CLI.exe"

& $cli `
  --hook EndField_0_8_25 `
  --game-root $gameRoot `
  --output-root $out `
  --vfs-db $db `
  --build-vfs-index
```

Probe metadata with resume when continuing a long run:

```powershell
& $cli `
  --hook EndField_0_8_25 `
  --output-root $out `
  --vfs-db $db `
  --probe-vfs-metadata `
  --resume
```

Useful seed terms:

```text
zhuangfy
chr_0030_zhuangfy
actor_zhuang
m_actor_zhuang
fangyi
```

## Acceptance

Current target scope is a focused dependency closure, not a full-game export:

- direct Zhuang Fangyi token hits
- Zhuang Fangyi materials
- shader and texture PPtr/CAB dependencies reachable from those materials
- supporting Mesh, GameObject, MonoBehaviour, AssetBundle objects only as evidence

Current accepted output snapshot:

- `16` `.mat`
- `39` `.png`
- `6` `.shader`
- `2810` `.hlsl`
- shader deadbeef refs: `0`
- texture deadbeef refs: `0`
- unresolved refs: `0`
- fallback shader success: `false`
- Unity `2021.3.34f1` batchmode open: exit code `0`

The `.mat` files must reference real exported Shader assets where the source dependency is available. Texture slots should use exact PPtr-derived matches before any name-based fallback. Remaining unresolved items must stay visible in `zhuangfy_unresolved_dependencies.json` and `.md`.

## Submodule State

- `AssetRipper`: `https://github.com/JinxiangW/AtCat-AssetRipper.git`, branch `master`
- `Source/Ruri.ShaderDecompiler`: `https://github.com/JinxiangW/AtCat-ShaderDecompiler.git`, branch `main`
- `Source/Ruri.RipperHook/AssetRipperGameHook`: branch `main`

Keep accepted submodule work on the default branch and remove temporary `codex/*` branches after merge/push.
