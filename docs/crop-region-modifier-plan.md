# Add Sticky Crop Region Modifier for Local Geometry Work

## Summary
Introduce a new Rhino modifier, `Crop Region`, that defines an active working region for downstream geometry stages. Its job is to make local terrain editing explicit: incoming geometry is cropped to the region, existing terrain outside the region is preserved unchanged, and downstream `Add Geometry` rebuilds operate only on the local patch before merging the untouched outside terrain back in.

Keep the current geometry-stage `Boundary` behavior as a separate concept, but rename or reword it in the UI so it reads as a triangulation perimeter rather than a vague crop or freeze tool.

## Key Changes
- Add a new modifier type, `CropRegionModifierDefinition`, serialized as a new modifier kind and grouped with the early geometry pipeline.
- Expose it in the panel as `Crop Region` with one source set for closed curves. The modifier itself does not generate mesh output; it updates active build context.
- Make the modifier sticky downstream: `Triangulate` and `Add Geometry` stages after it use the active crop region until another `Crop Region` modifier replaces it or the stack ends.
- Preserve current geometry-stage `Boundary`, but relabel and update help text to `TIN Boundary` or `Perimeter` so its role is explicit and no longer overlaps the crop concept.
- Increment schema version and add deserialization support for the new modifier type. No destructive migration is needed.

## Implementation Changes
- Build pipeline:
  - Add an `ActiveCropRegion` state to the build loop, carrying resolved closed boundary loops plus a fingerprint.
  - `Crop Region` updates that state and passes the mesh through unchanged.
  - Downstream geometry-stage cache fingerprints include the active crop-region fingerprint.
- Input cropping:
  - Points outside the active crop region are ignored.
  - Breaklines and contours that cross the crop region are clipped to the in-bounds portions; outside-only segments are discarded.
  - Multiple closed crop curves are treated as a union of working islands.
  - Invalid or open crop curves are ignored with diagnostics.
- `Triangulate` under crop:
  - Build only from cropped inputs.
  - Use the crop loops as the effective outer working perimeter for the local build instead of the old "largest closed boundary only" behavior.
- `Add Geometry` under crop:
  - Split the current mesh into inside and outside pieces using the crop region.
  - Rebuild only the inside patch from the in-bound existing mesh plus cropped incoming geometry.
  - Merge the rebuilt inside patch back with the untouched outside mesh.
  - Preserve hard constraints only for the rebuilt local region; outside topology remains unchanged.
- UI and UX:
  - Add `Crop Region` to modifier creation, card styling, summary text, and help text.
  - Rename existing geometry `Boundary` labels and help to reduce ambiguity.
  - Keep earthwork and cut-fill analysis `Boundary` unchanged in v1.

## Test Plan
- Unit tests for crop-region preprocessing:
  - inside points kept, outside points dropped
  - crossing curves clipped correctly
  - multiple crop loops behave as a union
  - open or invalid crop curves are ignored with warnings
- Rhino build-service tests:
  - `Triangulate` with crop builds only local terrain
  - `Add Geometry` with crop preserves outside mesh unchanged
  - sticky downstream behavior applies across multiple geometry stages
  - a later `Crop Region` modifier replaces the earlier active crop
  - changing crop input invalidates cached geometry stages
- Regression tests:
  - terrains without `Crop Region` keep current behavior
  - geometry `Boundary` or perimeter behavior stays unchanged apart from UI wording
  - earthwork and cut-fill boundary behavior stays unchanged

## Assumptions
- v1 scope is Rhino geometry stages only: `Triangulate` and `Add Geometry`.
- Crop behavior is explicit via a new modifier, not by redefining the existing geometry `Boundary` field.
- Crop region is a local-work tool, not a hard trim: outside terrain is frozen or preserved, not deleted.
- Multiple crop curves are supported as a union of closed working regions.
- Analysis boundaries and grading modifiers are out of scope for this first pass.
