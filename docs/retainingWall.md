# Retaining Wall Generator — Technical Specification

## 1. Overview

This Grasshopper component accepts an unordered set of open 3D curves defining retaining walls, modifies a terrain mesh to grade between wall edges, and outputs solid Breps representing the walls along with diagnostic messages.

Each wall is defined by a pair of 3D curves. Their XY offset defines wall thickness. Their Z values are authoritative and define wall height — the terrain is never queried for height. One curve is the toe (lower edge) and one is the top (upper edge), determined automatically. The wall solid is constructed by lofting rectangular cross-sections between synchronized stations on each pair.

---

## 2. Public API

### Inputs

| Name | Type | Description |
|---|---|---|
| Mesh (M) | Mesh | Terrain mesh — triangular faces only |
| Wall Curves (C) | List\<Curve\> | Unordered open 3D curves |
| Tolerance (T) | double | Pairing proximity threshold and sampling baseline (project units) |
| Sharpness (S) | double [0–1] | Controls profile steepness inside wall strip |
| Shoulder Width (SW) | double | Distance outside wall strip affected by grading |

### Outputs

| Name | Type | Description |
|---|---|---|
| Mesh (M) | Mesh | Modified terrain mesh |
| Wall Breps (W) | List\<Brep\> | Solid wall geometry |
| Pairs (P) | List\<Line\> | Preview lines connecting matched curve pairs (for user validation) |
| Report (R) | List\<string\> | Info / warning / error messages |

---

## 3. Curve Preprocessing

For each input curve, convert to polyline using chord tolerance = T/4, angle tolerance = 5°, minimum 8 segments. Store two forms:

- **Polyline3D** — preserves original Z values
- **Polyline2D** — same XY vertices, Z set to 0 (used only for pairing and mapping)

No vertical projection or terrain snapping is performed at this stage.

---

## 4. Pairing Algorithm

Curves are paired by mutual proximity in XY. Tolerance T is the defining threshold — all proximity checks are in project units.

### 4.1 Minimum Length Guard

Before scoring, check:

```
If min(ArcLength(A), ArcLength(B)) < 2T → skip with warning
```

Prevents unstable micro-walls from entering the pipeline.

### 4.2 Station Sampling

For each curve A, sample N stations evenly along XY arc length:

```
N = Clamp(round(ArcLength(A) / T), 8, 64)
```

### 4.3 Proximity Scoring

For each candidate curve B, compute:

```
WinningFraction = stations where B was nearest / N
DistanceIQR     = interquartile range of per-station distances to B
MeanDistance    = mean of all per-station distances to B
MaxDistance     = maximum per-station distance to B
Score           = WinningFraction / (1 + DistanceIQR / T)
```

### 4.4 Pair Confirmation

A and B form a valid pair if all four conditions are met:

- A's best scoring candidate is B **and** B's best scoring candidate is A (mutual match)
- MeanDistance ≤ T
- DistanceIQR ≤ T (rejects diverging or converging curves)
- MaxDistance ≤ 1.5T (rejects curves with localised bulges)

### 4.5 Minimum Thickness Guard

After confirmation, check average XY distance between paired curves:

```
If AvgDistance < T/10 → skip with warning: "Curves too close; thickness unstable"
```

Unmatched curves are reported as warnings. A Pairs output line is emitted for each valid pair so the user can visually confirm pairing before geometry is generated.

---

## 5. Corner Preprocessing

After pairing is complete, detect corners where two confirmed wall pairs share an endpoint. This step resolves corners in curve space so that the resulting lofted solids miter cleanly.

- For each pair of **confirmed wall pairs**, check whether any endpoints are within T of each other in XY.
- If a shared endpoint is detected, compute the XY intersection of the two curve directions.
- Trim or extend all four curves (both pairs) to meet at that intersection point.
- Snap the endpoint Z values of the trimmed curves to match at the intersection so the miter is watertight.
- Emit an info message for each corner resolved.

### 5.1 Crossing Wall Detection

Two wall pairs are considered crossing (not cornering) if:

- Their XY centerlines intersect at a **non-endpoint** location, **and**
- The intersection angle is **> 10°** (to exclude near-parallel overlaps), **and**
- The intersection lies within the interior parameter range of **both** pairs.

If crossing is detected, both walls are failed and a Grasshopper error bubble is emitted. No geometry is produced for either wall. All other walls continue processing.

---

## 6. Station Synchronization

### 6.1 Base Sampling

```
baseStep = Clamp(T * 2, minLength/64, minLength/8)
```

Stations include: uniform baseStep spacing, all polyline vertices, extra stations near turns >15°, and endpoints.

### 6.2 Normal Projection Mapping

At each station on curve A, compute the local tangent and XY perpendicular normal. Test both +normal and −normal directions, raycasting each to intersect curve B in XY. Choose the intersection with the smallest absolute distance. Reject the station if both directions miss, and fall back to closest-point search in a sliding parameter window.

Monotonicity is enforced: backward jumps >5% arc length are flagged. If 5 or more consecutive failures occur, the wall is split into sub-pairs. Each sub-pair is processed independently through all downstream steps and emitted as a separate Brep.

---

## 7. Toe / Top Determination

For each pair, compute mean Z over the middle third of synchronized stations. The rail with lower mean Z is the toe; the rail with higher mean Z is the top. If means are equal, curve A becomes the toe and an info message is emitted.

---

## 8. Wall Solid Construction

At each synchronized station i, construct four 3D points forming a closed rectangular cross-section. Toe/top assignment is applied before building loops — if A is the top rail, swap roles accordingly:

```
P0 = (A_xy, zLowOnA)    // A face, low side
P1 = (A_xy, zHighOnA)   // A face, high side
P2 = (B_xy, zHighOnB)   // B face, high side
P3 = (B_xy, zLowOnB)    // B face, low side
Loop: P0 → P1 → P2 → P3 → P0
```

Where zLowOnA is the lower Z value at A's station and zHighOnA is the higher, regardless of which curve was designated toe. This makes the section construction invariant to toe/top assignment direction.

### 8.1 Loft

Loft all section loops using `LoftType.Straight`, `closed = false`.

### 8.2 End Caps

At the first and last station, attempt a planar cap. If planar fails, construct a ruled cap surface. Join all faces into a closed solid Brep. If the result is not closed, emit a warning and return best-effort geometry.

---

## 9. Mesh Grading

The intent of mesh grading is to reconcile the terrain to the wall. The toe rail defines the terrain height at the base of the wall. The top rail defines the terrain height at the wall crown. Sharpness and shoulder width control the transition profile. The terrain is always pulled to match the curves — if the toe curve sits above the original terrain, the terrain is raised to meet it.

If grading fails for a single wall, that wall's grading is skipped and an error is emitted. All other walls continue processing.

### 9.1 Constraint Insertion

For each wall, insert toe and top rails as constrained segments, cross-segments between synchronized stations, and deterministic diagonals (always A[i] → B[i+1]). Retriangulate the mesh. If retriangulation fails, emit an error, preserve the original mesh for this wall, and continue with remaining walls.

### 9.2 Inside Strip Classification

A mesh vertex is considered inside the wall strip if its XY projection lies within the quadrilateral formed by A[i], A[i+1], B[i+1], B[i] for any station interval i. Test all intervals; the vertex is inside if any test passes.

### 9.3 Inside Strip Grading

For vertices inside the wall strip, compute lateral position u between toe (u=0) and top (u=1). Apply sharpness blend:

```
SmoothStep3(u) = u²(3 - 2u)
t = u + S * (SmoothStep3(u) - u)
Z = zToe + t * (zTop - zToe)
```

### 9.4 Shoulder Grading

If SW > 0, for vertices within SW of a rail on either side:

```
falloff = 1 - (d / SW)²
Z = Z_original + falloff * (Z_rail - Z_original)
```

To determine which side a vertex belongs to, compute the signed distance from the vertex to the local cross-segment vector at the nearest station. Positive sign = top side; negative sign = toe side. Shoulders are computed and applied independently per side.

No smoothing may operate across toe rails, top rails, cross-segments, or strip diagonals.

---

## 10. Data Structures

```csharp
struct PairCandidate {
    int    CurveIndex;
    double WinningFraction;
    double DistanceIQR;
    double MeanDistance;
    double MaxDistance;
    double Score;
}

struct WallPair {
    int  CurveA;
    int  CurveB;
    bool DirectionFlipped;  // true if B was reversed to align with A
}

struct StationMap {
    double[]  ParamsA;
    double[]  ParamsB;
    Point3d[] PointsA;
    Point3d[] PointsB;
}
```

---

## 11. Failure Handling

| Condition | Behavior |
|---|---|
| Curve shorter than 2T | Warning — skip curve |
| No mutual mate | Warning — skip curve |
| Curves too close (avg < T/10) | Warning — skip pair |
| Curves cross through each other | Grasshopper error bubble — both walls skipped, others continue |
| Retriangulation fails | Error — preserve original mesh for this wall, others continue |
| Brep join not closed | Warning — return partial Brep |
| ≥5 consecutive monotonic failures | Split into sub-pairs, process independently |
| <5 monotonic failures | Clamp parameter + Info message |
| Corner resolved successfully | Info message |

---

## 12. Assumptions

- Input curves are open.
- Curve Z values are authoritative. Intentionally inconsistent Z values are expected and valid — this is by design.
- Terrain is only modified, never queried for wall height.
- Paired curves must remain within T of each other throughout their length. Diverging, converging, or non-proximate curves will not be paired.
- Wall loft is ruled, not smooth.
- Component does not validate constructability — only geometry.
- Invalid input (crossing walls, unpairable curves) produces a "fix input" message. The component does not attempt to repair bad input.
- All failures are local. One wall failing never aborts processing of other walls.