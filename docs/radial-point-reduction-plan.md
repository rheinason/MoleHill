# Radial point-reduction plan

## Goal

Reduce dense spot-point inputs before TIN construction so interactive builds and baked/rendered terrain
use fewer vertices away from the area of interest, while preserving terrain shape, boundaries, contours,
breaklines, and grading reliability.

This should be an opt-in Triangulate setting. The first release should not silently change existing
terrain definitions.

## Recommendation

Use a **radially varying, error-aware sampler**, not distance-from-centre alone. Distance controls the
target point spacing; local terrain error decides whether a point is safe to remove. This avoids erasing
an important ridge, hollow, or isolated elevation point simply because it lies near the terrain edge.

The default focus should be the XY centroid of the active Triangulate boundary, falling back to the spot
point bounding-box centre. A later iteration can allow a picked focus point for views or sites whose area
of interest is off-centre.

## Proposed settings

- `Point reduction`: off by default.
- `Focus`: automatic initially; optional picked XY point in a later iteration.
- `Inner radius`: full-density region around the focus. Default to 25% of the input bounding radius.
- `Outer spacing multiplier`: target spacing at the terrain edge, initially capped at 4x.
- `Maximum vertical error`: hard accuracy limit, expressed in model units. A useful automatic default is
  tied to the terrain tolerance/detail-size policy, but it must remain visible and editable.
- `Preview reduction only`: defer. Preview/final topology divergence would weaken cache reuse and make
  the preview less representative of the baked result.

## Algorithm

1. Resolve point, contour, breakline, and boundary sources as today.
2. Apply the existing work-boundary crop first so points outside the work area incur no reduction cost.
3. Mark all constraint-derived vertices as protected. The reducer receives spot points only; boundary,
   contour, and breakline sampling remains exact and continues through `BreaklineDiscretizer`.
4. Estimate the base spot spacing robustly from sampled nearest-neighbour distances.
5. Compute normalized radial distance from the focus using the work-boundary/bounding-box extent. Use a
   smooth ramp from `baseSpacing` inside the inner radius to
   `baseSpacing * outerSpacingMultiplier` at the edge.
6. Bin spot points in a deterministic spatial grid whose cell size follows that local target spacing.
   Keep a stable representative in every occupied cell, plus local Z minima and maxima where they are
   materially different.
7. Validate each proposed removal against a local plane/triangle estimate built from retained neighbours.
   Restore the point when predicted vertical error exceeds `Maximum vertical error` or when the local
   slope/curvature classification marks it as significant.
8. Pass the retained spot points into the existing `PointCloudProcessor.Merge` path. Include every setting
   and the retained point set in the resolved-input fingerprint so stage caching remains correct.
9. Report `input -> retained`, reduction percentage, estimated maximum/95th-percentile vertical error, and
   elapsed time in the Triangulate diagnostic/timing output.

The implementation belongs in a pure Core type such as `Processing/AdaptivePointReducer.cs`, with a
small immutable options/result API. Rhino should only resolve the focus/settings and call it.

## Invariants and rejection gates

- Never remove or move boundary, contour, or breakline vertices.
- Deterministic output for identical input and settings, independent of source enumeration order.
- Preserve at least three non-collinear spot/constraint vertices and never turn a valid input invalid.
- No accepted reduction may exceed the configured sampled vertical-error limit.
- The reduced TIN must pass the same topology validation and cleanup cascade as the full input.
- If validation fails, automatically retry the unreduced spot set and emit a diagnostic.

## Delivery stages

1. **Core prototype and tests**: deterministic radial grid, protected-point handling, extrema retention,
   and local error restoration. Test flat, planar slope, ridge, bowl, noisy scan, sparse edge, duplicate,
   and constraint-heavy cases.
2. **Offline benchmark**: compare vertex/face count, TIN time, peak allocation, elevation RMSE/max error,
   contour displacement, and slope-summary drift on representative small/large terrains. Do not expose UI
   until this demonstrates a useful speed/accuracy trade-off.
3. **Rhino integration**: add persisted Triangulate settings, fingerprints, diagnostics, serializer coverage,
   and a panel summary. Keep the feature off for existing and new terrains until defaults are agreed.
4. **Visual validation**: compare shaded/wireframe views and contour overlays in Rhino, including an
   off-centre area of interest. Confirm downstream Grade Pad/Path and Remesh remain watertight.
5. **Optional focus picker**: add only if automatic boundary-centroid focus proves insufficient in real
   projects.

## Initial acceptance target

On a representative dense point cloud, aim for at least a 50% spot-point reduction and a meaningful
Triangulate speed-up while keeping sampled vertical error below the configured limit, contour displacement
within the same horizontal error budget, and all existing grading/topology tests green. The benchmark,
not a fixed radial formula, should determine the shipped defaults.
