# MoleHill.Rhino/Registry

The Blender-style **type registry**: each terrain type ships one self-describing descriptor and the app
discovers it everywhere by reflection — no central switches to edit. Adding a type = drop one descriptor
(+ its `Model/*Definition.cs`). See `docs/architecture.md`.

There are four parallel families, each with a `*TypeDescriptor` base + reflection-discovered
`*TypeRegistry`:
- **Modifiers** — `ModifierTypeDescriptor` (richest: factory + `RunBuildStage` build dispatch + `Parameters`
  schema cards + menu chrome). Detailed below.
- **Objects** — `ObjectTypeDescriptor`/`ObjectTypeRegistry` (factory + card chrome + `Parameters` schema
  rows). Scatter keeps only its weighted block-mix editor as a small custom row.
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
- **Card** — `Parameters` (an ordered `ParameterDescriptor<ModifierDefinition>` list) is turned into Eto editor rows by
  `MoleHillPanel.Schema.cs`. The same schema is the contract for future **Grasshopper-component**
  generation (GH parity).

## Parameter schema

Every family declares its card rows as `ParameterDescriptor<TDefinition>` — **one generic type for all
four**, closed over the family's definition root (`ModifierDefinition`, `AnalysisDefinition`,
`AnnotationDefinition`, `TerrainObjectDefinition`). It used to be four hand-cloned copies; they drifted
(the annotation copy's doc comments still described analyses, and colour fallbacks had two incompatible
signatures), which is what the merge fixes. The type argument is what keeps the families apart now: an
analysis accessor cannot be handed an annotation, so the schemas cannot be mixed and cannot drift.

Each descriptor-declaring file aliases its closed type for brevity:

```csharp
using AnnotationParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.AnnotationDefinition>;
// ... AnnotationParam.Number("Interval", "Interval", a => …, (a, v) => …, min: 0.01)
```

- **`Kind`** (`ParameterKind`) — Sources / Number / OptionalNumber / Slider / Bool / ReadOnly / Choice /
  Color / ColorRamp / Text / BlockMix. Use the static factories (`Number`, `Slider`, `Sources`, …) rather
  than the object initializer; they set the accessor pair each kind needs.
- **Accessors** — typed get/set delegates against the concrete definition (cast inside, mirroring the old
  hand-written card mutations). `ParameterSchemaGuardTests` asserts that every declared row actually
  carries the pair its kind requires, across all four families.
- **`VisibleWhen`** (`Func<TDefinition,bool>?`) — hides a row when it returns false, so a parameter can
  depend on another field: the Remesh Min Angle / Max Area rows only show when `Mode == "rebuild"`. It
  re-evaluates on every card rebuild, and since any edit (including the gating `Choice` dropdown) rebuilds
  the card via `StateChanged → RefreshUi`, gated rows appear/disappear live.
- **`LabelFor` / `ChoiceOptionsFor`** — override the label or option list when either depends on live
  definition state (the slope-unit suffix on range labels; the value-format dropdown's "Custom" entry).
- **Commit hints** — `LiveScrub`, `LiveEdit`, `RefreshOnly`, `IncrementalCommit`, `RebuildAfterCommit`.
  These are **inert data on the descriptor**: it never acts on them. Each family's commit closure in
  `MoleHillPanel.Schema.cs` reads the ones it honours, which is how the families keep different
  save/rebuild semantics off a shared type. Leaving one unset is how a family opts out.
  - `RefreshOnly` — cheap preview recolor via `MutateAndRefreshAnalysis` (palette/range on Slope,
    Elevation, Cut-Fill).
  - `IncrementalCommit` — skip the full rebuild and run the type's own incremental rebuild; today only
    Contour, via `TerrainController.RebuildContourAnalysis` / `RefreshContourColor`.
  - `LiveScrub` — defer the document save and suppress the UI refresh mid-drag, so a save's
    `StateChanged` cannot rebuild the card out from under the gesture.
  - `LiveEdit` — commit per keystroke instead of on focus loss (objects).
  - `RebuildAfterCommit` — relayout the owning tab afterwards, for edits that change which rows exist.
- **Colour rows** — `FallbackColor` and `ColorDefaultTextFor` both take `(TerrainDefinition, TDefinition)`
  so a row can resolve "by layer" against the item's own output layer and fall back to the terrain's layer
  for that family.

Two kinds are whole controls rather than a labelled widget, and the row builder renders them through a
per-family bespoke hook:

- **`ColorRamp()`** — analyses only. Stops, linear/stepped mode, band interval and the mapped range in one
  card, rendered as `UI/ColorRampControl`. It needs no accessors because every field it edits lives on
  `AnalysisDefinition` itself, so declaring it is the whole of "this analysis is colour-mapped". Always a
  refresh-only commit — re-running an analysis to change a swatch would make dragging a stop unusable.
  Slope, Elevation and Cut/Fill each declare one. Annotations draw rather than colour-map, so none has it.
- **`BlockMix()`** — Scatter alone; the weighted block-definition editor.

`ObjectParameterCatalog.cs` declares the rotation/scale/seed/offset rows every terrain-object type shares
once; `ObjectTypeDescriptor.Parameters` defaults to `ObjectParameterCatalog.Common` (not an empty list, so
object types that declare nothing still get the shared rows), and Scatter appends `CommonTransforms` to
its own. `AnnotationParameterCatalog` in `AnnotationDescriptors.cs` does the same for annotations
(`VerticalExaggeration()`, `SlopeUnitChoice()`, and the `BlockAttributeTail<TAnalysis>()` iterator that
yields the shared value-format/prefix/suffix/scale/colour tail).

Other pieces in this folder:

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
