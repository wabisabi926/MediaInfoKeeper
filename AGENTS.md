# MediaInfoKeeper Agent Guide

## Goal
- Keep patches stable across Emby upgrades.
- Prefer exact method resolution over heuristic matching.

## Docs Version Variable
- `EMBY_DOCS_DIR`: specifies which folder under `Docs/` is used as the signature source.
- Default: `emby_4.9.3.0`.
- Effective path: `Docs/${EMBY_DOCS_DIR}/`.

## Required Inputs
- Use method signature exports under `Docs/${EMBY_DOCS_DIR}/`.
- Primary files: `*_methods.txt` for target assemblies.

## DLL Source Location
- Local Emby DLL files are at `/Users/honue/Documents/Emby/dlls`.
- Use this path when inspecting assemblies, resolving types, or cross-referencing method signatures.

## Project Structure
- `Patch/`: Harmony patch implementations and method resolution logic.
- `Patch/PatchManager.cs`: central patch bootstrap, configure, and health tracking entry.
- `ScheduledTask/`: operational tasks (export, scan, refresh, diagnostics).
- `Services/`: runtime business services used by patches and tasks.
- `Configuration/` + `Options/`: plugin config models and UI wiring.
- `Docs/${EMBY_DOCS_DIR}/`: selected versioned method signature exports; the only signature source of truth.
- `Scripts/`: helper scripts for pulling dlls/method docs and runtime ops.

## Patch Rules
- Always use `PatchMethodResolver.Resolve(...)` for patch targets.
- Set explicit `ParameterTypes` whenever possible.
- Set `ReturnType` when overload ambiguity is possible.
- Avoid `Predicate` unless there is no stable type-based signature.
- Do not use fallback `FindMethod(...)` name-only matching in production paths.

## Workflow
1. Locate target type and method in `Docs/${EMBY_DOCS_DIR}/<Assembly>_methods.txt`.
2. Copy exact parameter type order from docs.
3. Resolve dependent runtime types by full name (`Assembly.GetType("Namespace.Type")`).
4. Build `MethodSignatureProfile` with exact `BindingFlags` + `ParameterTypes`.
5. If key dependent type is missing, log `PatchLog.InitFailed(...)` and return.
6. Build and verify with `dotnet build MediaInfoKeeper.sln`.

## Logging
- On resolve success/failure, rely on existing `PatchLog.ResolveHit/ResolveFailed`.
- On missing prerequisite types, provide clear reason in `InitFailed`.

## Compatibility Notes
- When a method has multiple valid overloads across versions, resolve each exact overload and patch all of them.
- Keep patch prefix/postfix signatures compatible with all patched overloads.

## Don’ts
- Don’t rely on parameter count (`p.Length`) as primary matching strategy.
- Don’t keep dead reflection helpers that are no longer used.
- Don’t silently swallow missing type/signature situations.
- Don’t mix unrelated refactors when updating patch signatures.
