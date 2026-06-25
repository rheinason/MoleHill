# Grasshopper parity — design

Status: **proposal, not yet implemented.** Goal: let registry-described terrain types surface as
Grasshopper components with as little per-type duplication as possible, building on the
`ParameterDescriptor` schema introduced for the Rhino panel.

## The reality (why this is a feature, not a wrapper)

Two facts from the current code shape everything:

1. **GH components call Core directly.** `MoleHill.Grasshopper/Components/*` (e.g. `GradePadComponent`)
   call `PadGrader.Grade`, `TinEngine`, `TriangulationHelper`, etc. on flat arrays. They do **not** use
   `MoleHill.Rhino`'s `TerrainBuildService` (which is bound to `RhinoDoc`, runtime caches, the display
   conduit, and document persistence). So there is no existing "run one modifier" entry point to wrap.

2. **The panel schema and the GH contract are not the same shape.** Example — Grade Pad:

   | | Panel `Parameters` (Rhino) | GH `GradePadComponent` |
   |---|---|---|
   | inputs | Boundaries, SlopeAngle, CutSlopeAngle, MaxDistance | Mesh, Boundaries, Slope, MaxDistance, **Lock Curves**, **Corner Segments**, **Fill Slope** |
   | data access | single value per field | **per-pad lists** ("shorter lists repeat last value") |
   | geometry prep | source-set picks resolved by the build pipeline | **plane-fit each boundary curve**, tessellate, build `PadGrader.PadBoundary` |
   | outputs | (none — mutates terrain) | Mesh, **Cut/Fill/Net volume** |

   The panel schema deliberately models *terrain-stack editing*; the GH component models a *stateless
   mesh-in/mesh-out function* with richer knobs and numeric outputs. Simple types (Remesh:
   EdgeLength/MaxArea/MinAngle) line up closely; grading/section types diverge a lot.

**Conclusion:** the panel `Parameters` list cannot, as-is, generate the GH components. We need a
GH-facing contract on the descriptor, distinct from (but reusing where possible) the panel schema.

## Options

### Option A — One superset schema drives both panel and GH
Extend `ParameterDescriptor` until it can express everything both surfaces need: list access, geometry
inputs with conversion hooks (curve→plane, curve→polyline), output ports, and "this input is mesh /
terrain context". Generate the panel card AND a generic GH component from the one list.

- *Pros:* true single source of truth; adding a type lights up both surfaces.
- *Cons:* the schema balloons (mesh I/O, lists, plane-fit, volume outputs, optionality, repeat-last
  semantics) and starts encoding Core call details. High risk of an over-general abstraction that's
  harder to read than the 8 small hand-written components it replaces. The panel would carry GH-only
  concepts (outputs, mesh input) it doesn't use.

### Option B — Separate GH contract on the descriptor (recommended)
Add an optional GH facet to `ModifierTypeDescriptor` (and later the other families):

```
GhComponentSpec? Grasshopper { get; }   // null = no GH component for this type

sealed class GhComponentSpec {
    string Name, Nick, Category, SubCategory, Description; Guid ComponentGuid;
    IReadOnlyList<GhPort> Inputs;        // name/nick/desc/access/type/optional
    IReadOnlyList<GhPort> Outputs;
    Action<GhSolveContext> Solve;        // reads inputs, calls Core, sets outputs
}
```

A single generic `RegistryTerrainComponent : GH_Component` is instantiated once per descriptor that has a
spec; `RegisterInputParams`/`RegisterOutputParams` walk `Inputs`/`Outputs`, and `SolveInstance` calls
`spec.Solve`. The Core-call body (the interesting part of today's `SolveInstance`) moves into `Solve`
nearly verbatim.

- *Pros:* one descriptor file per type still owns everything (panel + serialization + GH); the GH spec
  stays honest about the GH contract instead of forcing it through the panel schema; `GhPort` maps 1:1
  to `GH_InputParamManager`. Shared helpers (mesh↔flat-array, curve→polyline) extract to one place,
  removing the copy-paste across the 8 components.
- *Cons:* two schemas on the descriptor (panel `Parameters` + `Grasshopper`). They reference the same
  definition properties, so they stay close but aren't literally one list.

### Option C — Keep hand-written components, just auto-register them
Discover `GH_Component` subclasses by reflection / a marker attribute; no shared param plumbing.

- *Pros:* tiny. *Cons:* doesn't reduce the per-component boilerplate the plan was about; near-zero payoff.

## Recommendation

**Option B.** It delivers the plan's payoff (a registered type appears in GH, Core-call logic lives with
the type, shared geometry plumbing deduped) without distorting the panel schema into a GH dialect. Keep
`Parameters` (panel) and `Grasshopper` (GH) as sibling facets on the descriptor.

`MoleHill.Grasshopper` would take a reference to the registry. The registry currently lives in
`MoleHill.Rhino` (RhinoCommon only — no Grasshopper dependency), but `ModifierDefinition` and the
descriptors live there too. **Open question to resolve first:** the registry + descriptors may need to
move to a neutral assembly (e.g. a new `MoleHill.Registry`, or into `MoleHill.Core`) so both
`MoleHill.Rhino` and `MoleHill.Grasshopper` can reference them. Today `MoleHill.Grasshopper` does not
reference `MoleHill.Rhino`, and it shouldn't (Rhino-plugin vs GH-plugin). This assembly placement is the
main structural decision before coding.

## Incremental plan (once Option B + assembly placement are agreed)

1. **Assembly move:** relocate `Registry/` + `Model/*Definition.cs` to the shared assembly; fix refs.
   Build clean; serialization round-trip unchanged (Rhino still owns `TerrainJsonTypeResolver`).
2. **GH facet types:** add `GhComponentSpec`/`GhPort`/`GhSolveContext` + shared mesh/curve conversion
   helpers (extracted from existing components).
3. **Generic component:** `RegistryTerrainComponent`; register one instance per descriptor with a spec.
4. **Vertical slice:** give **Remesh** a `Grasshopper` spec (closest panel↔GH match), delete
   `RemeshComponent`, verify in GH (component appears, solves, matches old outputs).
5. **Migrate the rest** one per commit: Grade Pad, Grade Path, In-Situ Stair, Retaining Wall, TIN,
   Smooth, Mesh Areas/Collage — moving each `SolveInstance` body into its `Solve` and deleting the
   hand-written component. Keep `ComponentGuid`s identical so existing GH definitions keep resolving.
6. **Objects/markers/analyses:** only where a GH component makes sense (most analyses are doc-oriented).

## Verification

- `tests/MoleHill.Grasshopper.Tests` (exists) — add: every descriptor with a `Grasshopper` spec yields a
  component whose input/output port counts + `ComponentGuid` match the legacy component; solve-equality
  on a fixture mesh for the migrated types.
- Build clean + Core 284 + GH tests after each step; load the `.gha` in Rhino/GH and confirm each
  component appears on the MoleHill tab and solves (rhino-mcp `g1_*` Grasshopper tools).
- **Keep every `ComponentGuid` stable** — like the JSON discriminators, these are the on-disk identity
  that existing `.gh` files reference.

## Risks

- **`ComponentGuid` drift** breaks existing GH definitions — pin them in the specs, assert in tests.
- **Over-abstraction:** if `GhComponentSpec` starts needing many per-type escape hatches, that's a signal
  the type is better left hand-written; allow `Grasshopper => null` and keep a bespoke component.
- **Assembly move churn:** the registry relocation touches many `using`s; do it as its own commit with no
  behavior change, verified by the existing serialization round-trip before adding any GH code.
