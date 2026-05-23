# Ruri-RipperHook Agent Brief

## Purpose

This is the only repository-level agent entrypoint. Keep it short. Put detailed workflow notes in `docs/`, especially `docs/endfield-export-workflow.md`.

## Non-Negotiables

- Describe this project as a normal unpacking, export, and data-processing tool.
- Do not document private game-specific implementation details or print literal key material in docs, logs, reports, or summaries.
- Final implementation changes must live under `Source/Ruri.*/**`.
- Do not edit `AssetRipper/**` or other upstream submodules for final work. Temporary probing is allowed only if reverted before delivery.
- Do not add game-specific branches to shared upstream code. Use Ruri hook modules for game/version behavior.
- Prefer repo-relative paths in docs and scripts. Use environment variables for external installs or local data roots.

## Hook Rules

- Install method hooks through `Ruri.Hook` only.
- Use classes derived from `RuriHook` with `[RetargetMethod]`, `[RetargetMethodFunc]`, or `[RetargetMethodCtorFunc]`, and invoke `Initialize()` at startup.
- Do not instantiate `MonoMod.RuntimeDetour.Hook` or `ILHook` directly outside `Ruri.Hook`.
- Canonical examples:
  - `Source/Ruri.RipperHook/AssetRipperHook`
  - `Source/Ruri.AssemblyDumper/Pipeline/ArAssemblyDumperHook.cs`

## Endfield Facts

- Active repo is the current repository root.
- Historical repo references should be treated as stale unless the user explicitly asks.
- Local validation data root: set `END_FIELD_GAME_ROOT` to the Endfield install directory.
- Hook id: `EndField_0.8.25`; CLI accepts `--hook EndField_0_8_25`.
- Endfield hook code: `Source/Ruri.RipperHook/AssetRipperGameHook/UnityHypergryph/Endfield/`.
- CLI VFS/CAB workflow code: `Source/Ruri.RipperHook.CLI/Cli/`.
- Output root: `TestLoopOutput\`.
- Current focused run root: `TestLoopOutput\EndfieldZhuangFullRun\`.
- Current accepted scope: focused Zhuang Fangyi export (`zhuangfy`, `chr_0030_zhuangfy`) plus material PPtr/CAB dependency closure, not full-game export.
- Full handoff runbook: `docs/endfield-export-workflow.md`.

## Validation

```powershell
dotnet build .\Ruri-RipperHook.slnx -c Release

& .\Source\Ruri.RipperHook.CLI\bin\Release\net10.0-windows\Ruri.RipperHook.CLI.exe `
  --list-hooks
```

- Long-running test output must stay under `TestLoopOutput/`.
- Do not stage generated output folders.
- Kill stale `Ruri.RipperHook.CLI.exe` processes before starting a new long CLI run.

## Submodules

- `AssetRipper`: `https://github.com/JinxiangW/AtCat-AssetRipper.git`, branch `master`.
- `Source/Ruri.ShaderDecompiler`: `https://github.com/JinxiangW/AtCat-ShaderDecompiler.git`, branch `main`.
- `Source/Ruri.RipperHook/AssetRipperGameHook`: branch `main`.
- Keep AtCat forks on their default branch unless the user asks for another branch.
