# MoleHill.Rhino/Registry

The Blender-style **type registry**: each modifier type ships one self-describing descriptor and the app
discovers it everywhere by reflection — no central switches to edit. Adding a modifier = drop one
descriptor (+ its `Model/*Definition.cs`). See `docs/architecture.md`.

## Core
- `TerrainTypeRegistry.cs` — static registry; its static ctor reflects over this assembly, instantiates
  every concrete `ModifierTypeDescriptor`, and indexes them by CLR type and by `Kind`. Lookups:
  `Modifiers`, `ForModifierType(Type)`, `ForModifierKind(string)`, `CreateModifier(kind, unitSystem)`.
- `ModifierTypeDescriptor.cs` — the abstract per-type contract. Declares: `Kind` (the JSON discriminator —
  **must stay stable** so saved `.3dm` files load), `DefinitionType`, `DisplayName`/`IconName`/`Subtitle`/
  `SortOrder`/`CanCreateFromMenu` (menu + card chrome), `Create(unitSystem)` (unit-aware defaults),
  `RunBuildStage(context)` (the build step), and `Parameters` (the card schema).

## What a descriptor drives
- **Factory** — `TerrainController.CreateModifier` → `Registry.CreateModifier`.
- **Panel** — add-menu, icon, subtitle, label all read the descriptor (`MoleHillPanel`).
- **Build** — `TerrainBuildService.Build` dispatches `descriptor.RunBuildStage(ctx)` instead of a switch;
  each descriptor forwards to a `TerrainBuildService.RunXStage` shim (see `Services/*.ModifierStages.cs`).
  `ModifierBuildContext.cs` carries the per-stage mesh/fingerprint in/out.
- **Card** — `Parameters` (an ordered `ParameterDescriptor` list) is turned into Eto editor rows by
  `MoleHillPanel.Schema.cs`. The same schema is the contract for future **Grasshopper-component**
  generation (GH parity).

## Parameter schema
- `ParameterDescriptor.cs` — one declarative input: `Kind` (Sources/Number/OptionalNumber/Slider/Bool/
  Layer/ReadOnly), `Label`/`Help`, numeric bounds, and typed get/set accessor delegates against the
  concrete definition (cast inside, mirroring the old hand-written mutations). Use the static factories
  (`Number`, `Slider`, `Sources`, …) to keep schemas terse.
- Rows the schema can't express (Triangulate work-area picker, geometry-input boundary-peel block) are
  appended by `MoleHillPanel.AppendBespokeModifierRows` — the custom-draw escape hatch.

## Adding a modifier
1. Add `Model/<Name>ModifierDefinition.cs` (no `[JsonDerivedType]` — the resolver registers it from the
   descriptor's `Kind`).
2. Add `<Name>ModifierDescriptor.cs` here: set `Kind`/metadata, `Create`, a `RunXStage` shim in
   `Services/TerrainBuildService.ModifierStages.cs`, and a `Parameters` schema.
3. That's it — serialization, menu, factory, build dispatch, and card all pick it up via reflection.

## Serialization
`Services/TerrainJsonTypeResolver` builds `ModifierDefinition` JSON polymorphism from this registry
(descriptor `Kind` = discriminator) plus two deserialize-only legacy shims (`mesh-areas`,
`mesh-collage`). It's wired into `TerrainSerializer.SharedOptions`, used by every whole-terrain
(de)serialization site (save/load + the clone paths). Keep each `Kind` stable — it's the on-disk
discriminator.
