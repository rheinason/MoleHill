# Tolerance Cleanup MVP

Date: April 20, 2026

## Progress snapshot (2026-07-11)

Status: implementation complete in the Rhino build path; final user-facing validation remains.

- **Phase 1 complete:** `TerrainTolerancePolicy` derives bounded input, curve, remesh, Grade Path,
  Grade Pad, and retaining-wall tolerances from terrain detail size plus document tolerance.
- **Phase 2 complete:** build diagnostics report the resolved profile, and `TerrainBuildHeuristicsTests`
  cover legacy-detail migration, unit conversion, and the Grade Path/Pad bounds.
- **Acceptance still open:** run coarse and dense real Rhino cases, confirm users no longer need to tune
  the global value for grading, then archive this MVP document if the results hold.

## Summary

Tolerance handling is currently too hard for users to reason about.

A single terrain/global tolerance is effectively being used for multiple different jobs:

- input coincidence and snapping
- constraint deduplication
- remesh seed reuse
- seam comparison
- grading-local path and pad geometry decisions

That coupling is creating two problems:

1. Users do not have a clear mental model for what changing tolerance will actually do.
2. Some grading operations already need internal overrides or clamps to remain stable, which means the user-facing setting is no longer the full truth anyway.

This MVP should make tolerance behavior more predictable without attempting a full geometry-kernel rewrite.

## Goals

- Reduce user confusion around tolerance settings.
- Stop requiring users to tune one global tolerance to make `Grade Path` work.
- Keep grading tools stable across coarse and dense meshes.
- Preserve current successful path-specific tolerance safeguards.

## Non-Goals

- Do not redesign every modifier's geometry pipeline in one pass.
- Do not expose a large matrix of advanced tolerance controls to users.
- Do not remove internal safety clamps where they are already preventing failures.

## Current Problem

Today, the effective tolerance seen by a grading operation can come from multiple places:

- `terrain.GlobalTolerance`
- Rhino model absolute tolerance
- operation-local minimum tolerances
- operation-local clamps
- remesher-local caps tied to target edge length

This means two confusing things are true at once:

- tolerance feels global in the UI
- tolerance is already partially specialized in code

`Grade Path` is the clearest example. It now uses a tighter internal geometry tolerance than the terrain/global tolerance because the broader shared value was causing remesh constraint collapse.

## MVP Proposal

### 1. Narrow the meaning of the user-facing terrain tolerance

The terrain/global tolerance should primarily mean:

- how aggressively nearby input geometry is considered coincident
- how much snapping/dedup is allowed when preparing terrain inputs

It should not be treated as the direct working tolerance for every grading algorithm.

### 2. Add operation-local effective tolerance profiles

Each major subsystem should derive its own effective tolerance from local scale plus bounded global input tolerance:

- terrain input preparation
- remeshing / constraint insertion
- pad grading
- path grading
- stitch / seam validation

The main point is that these should be computed internally and consistently, not improvised ad hoc inside each call site.

### 3. Default to automatic operation-local tolerance

For grading tools, the default behavior should be automatic.

Example direction:

- derive effective tolerance from local feature scale
- clamp it to safe numeric bounds
- log the effective tolerance in diagnostics when useful

This keeps the system predictable for users while still letting the implementation stay robust.

### 4. Keep advanced override capability out of the main UI for now

If advanced overrides are needed later, they should come after the automatic model is coherent.

The MVP should not add more user-facing tolerance knobs.

## Suggested User Model After MVP

The user-facing explanation should become:

- `Terrain tolerance` controls how input geometry is matched and cleaned up.
- Grading tools use automatic internal tolerances based on the terrain and feature scale.
- Users should not normally need to adjust tolerance to make `Grade Path` or `Grade Pad` succeed.

That is a much simpler model than the current one.

## Implementation Slice

### Phase 1

- Introduce a small internal tolerance policy/helper.
- Centralize effective tolerance calculation for:
  - terrain input preparation
  - remesher constraint insertion
  - `Grade Path`
  - `Grade Pad`
- Replace scattered grading-local clamps with calls into that shared policy where practical.

### Phase 2

- Update diagnostics to report both:
  - user-facing terrain tolerance
  - effective operation-local tolerance
- Review whether any existing modifier-specific tolerance UI should be hidden, renamed, or documented more clearly.

## Success Criteria

The MVP is successful if:

- `Grade Path` no longer depends on users experimenting with global tolerance values.
- internal grading stability remains at least as good as the current path-specific clamp approach
- logs and diagnostics make the applied tolerance model understandable
- the UI exposes fewer confusing tolerance expectations, not more

## Risks

- Some workflows may currently rely on the old broad interpretation of terrain tolerance.
- Centralizing tolerance policy may reveal inconsistent assumptions in older modifiers.
- Diagnostic updates will be important; otherwise behavior will improve internally but still feel opaque.

## Recommended Next Step

When scheduled, start with a small internal `GradingTolerancePolicy` or similarly named helper and migrate `Grade Path` first, because it already demonstrates why the current user-facing model is misleading.
