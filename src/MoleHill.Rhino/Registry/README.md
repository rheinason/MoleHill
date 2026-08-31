# MoleHill.Rhino/Registry

The Blender-style **type registry**: each terrain type ships one self-describing descriptor and the app
discovers it everywhere by reflection — no central switches to edit. Adding a type = drop one descriptor
(+ its `Model/*Definition.cs`). See `docs/architecture.md`.

There are four parallel families, each with a `*TypeDescriptor` base + reflection-discovered
`*TypeRegistry`:
- **Modifiers** — `ModifierTypeDescriptor` (richest: factory + `RunBuildStage` build dispatch + `Parameters`
  schema cards + menu chrome). Detailed below.
- **Objects** — `ObjectTypeDescriptor`/`ObjectTypeRegistry` (factory + card chrome + `ObjectParameterDescriptor`
  schema rows). Scatter keeps only its weighted block-mix editor as a small custom row.
- **Markers** — `MarkerTypeDescriptor`/`MarkerTypeRegistry` (factory + add-button text/help).
- **Analyses** — `AnalysisTypeDescriptor`/`AnalysisTypeRegistry` (factory + menu grouping + card chrome +
  `Parameters` schema cards, same shape as modifiers). Per-type collapsed summary, computed
  summaries/legends, and the slope-unit-with-range-conversion editor aren't schema-expressible and stay
  bespoke in `MoleHillPanel.Analysis.cs` (`AppendBespokeAnalysisRowsBefore`/`After`).
- **Annotations** — `AnnotationTypeDescriptor`/`AnnotationTypeRegistry`, the same shape against
  `AnnotationDefinition`. Its bespoke rows (section sources, insertion-origin picker, comparison terrains)
  live in `MoleHillPanel.Annotations.cs` (`AppendBespokeAnnotationRowsBefore`/`After`). There is no
  `IsAnnotation` flag any more: which family a type belongs to is settled by which descriptor base it
  derives from, so the two cannot disagree. Analyses evaluate the terrain, annotations describe it — see
  `docs/architecture.md` → "Analysis vs annotation".

All five feed `Services/TerrainJsonTypeResolver`, which builds JSON polymorphism for every family from
its registry — there are **no `[JsonDerivedType]` lists** on the definition bases anymore. Discriminator
strings come from each descriptor's `Kind` and must stay stable so saved `.3dm` files load.

## Core
- `TerrainTypeRegistry.cs` — static registry; its static ctor reflects over this assembly, instantiates
  every concrete `ModifierTypeDescriptor`, and indexes them by CLR type and by `Kind`. Lookups:
  `Modifiers`, `ForModifierType(Type)`, `ForModifierKind(string)`, and unit-aware
  `CreateModifier(kind, ModelUnitContext)` (the `UnitSystem` overload remains for compatibility/tests).
- `ModifierTypeDescriptor.cs` — the abstract per-type contract. Declares: `Kind` (the JSON discriminator —
  **must stay stable** so saved `.3dm` files load), `DefinitionType`, `DisplayName`/`IconName`/`Subtitle`/
  `SortOrder`/`CanCreateFromMenu` (menu + card chrome), `Create(unitSystem)` (metre-authored defaults
  are converted through the registry's `ModelUnitContext` overload),
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
- `ParameterDescriptor.cs` — one declarative input against `ModifierDefinition`: `Kind` (Sources/Number/
  OptionalNumber/Slider/Bool/ReadOnly/Choice/Color/Text), `Label`/`Help`, numeric bounds, and typed
  get/set accessor delegates against the concrete definition (cast inside, mirroring the old hand-written
  mutations). Use the static factories (`Number`, `Slider`, `Sources`, …) to keep schemas terse.
  A `VisibleWhen` gate (`Func<ModifierDefinition,bool>?`) hides a row when it returns false, so parameters
  can depend on another field — e.g. the Remesh Min Angle / Max Area rows only show when `Mode ==
  "rebuild"`. It re-evaluates on every card rebuild, and since any edit (including the gating `Choice`
  dropdown) rebuilds the card via `StateChanged → RefreshUi`, gated rows appear/disappear live.
- `ObjectParameterDescriptor.cs` — the matching object-card vocabulary against `TerrainObjectDefinition`.
  Shared rotation/scale/seed controls are declared once; Scatter adds mode-dependent density, filter,
  preview, and block-mix descriptors.
- `AnnotationParameterDescriptor.cs` — the same shape against `AnnotationDefinition`, minus `ColorRamp`:
  annotations draw, they are never colour-mapped.
- `AnalysisParameterDescriptor.cs` — the same shape against `AnalysisDefinition` (separate type because the
  accessor delegates are typed differently). Adds two mutate-mode flags plain modifiers don't need:
  `RefreshOnly` (cheap preview recolor via `MutateAndRefreshAnalysis`, e.g. palette/range on Slope/
  Elevation/Cut-Fill) and `IncrementalCommit` (skip the full rebuild and run the type's own incremental
  rebuild — today only Contour, via `TerrainController.RebuildContourAnalysis`/`RefreshContourColor`).
  Also supports `LabelFor`/`ChoiceOptionsFor` overrides for rows whose label or option list depends on
  live definition state (e.g. slope-unit suffix on the range labels, the value-format "Custom" entry).
  `AnalysisParameterDescriptor.ColorRamp()` declares the whole colour block — stops, linear/stepped,
  band interval, mapped range — as one row, and needs no accessors because every field it edits lives on
  `AnalysisDefinition` itself. Declaring it is the whole of "this analysis is colour-mapped"; it is
  always `RefreshOnly`, since re-running an analysis to change a swatch would make dragging a stop
  unusable. Slope, Elevation and Cut/Fill each declare one.
- `AnalysisTypeDescriptor.DescribeBlocker(terrain, analysis)` — why this analysis cannot produce anything
  yet, or null when it is ready. Cut/fill needs a surface to compare against, waterflow needs start
  points, a section needs a curve; without them these types run, succeed, and emit nothing, and the card
  fills with plausible controls and placeholder numbers while nothing says which one is holding it up.
  The panel puts the message on a warning surface at the top of the card, above the controls, and it
  names the control to reach for. Phrase it as the thing to do, not as a failure.
- `LayerRoleRegistry.cs` (+ `LayerRoleDescriptor`, `LayerAppearanceDefaults`, `LayerRoleFacets`) — the
  single declaration of every output destination: stable id, parent, default path, appearance, and whether
  its colour is data or drafting. The shipped layer template is generated from it. See
  `docs/architecture.md` → "Output layer roles".
- `AnalysisFormatting.cs` — pure slope-unit/value-format/layer-color formatting helpers shared by the
  schema descriptors and the panel's hand-written rows. Lives here (not in `MoleHill.Rhino.UI`) so
  `MoleHill.Rhino.Tests`, which links `Registry/*.cs` directly without a UI reference, can still compile
  the analysis descriptors that use them.
- Rows the schema can't express (Triangulate work-area picker and GeoTIFF-surface import/replace action,
  geometry-input boundary-peel group), plus
  schema rows needing custom placement (Triangulate Contour Mode), are appended by
  `MoleHillPanel.AppendBespokeModifierRows` — the custom-draw escape hatch. The analysis
  equivalent is `AppendBespokeAnalysisRowsBefore`/`After` in `MoleHillPanel.Analysis.cs`.

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
