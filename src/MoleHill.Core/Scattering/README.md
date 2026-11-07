# MoleHill.Core/Scattering

Object-scatter point distribution (the Scatter object type). Pure, unit-tested; the Rhino side
(`TerrainBuildService.Scatter.cs`) projects the returned points onto the terrain and emits block
instances.

Plant/Orient also use this folder's pure placement math: `PlacementBox` computes a deterministic
bounded approximation to a minimum-volume box (principal-axis/world seeds, then volume refinement;
all supplied points contribute to final extents). `PlacementFrame` fits a terrain plane to footprint
hits and lifts the requested heading into that plane without changing its bearing in plan.
`PlacementVector` supplies double-precision coordinates; `PlacementLinearAlgebra` shares the symmetric
eigensolver. Degenerate/non-finite samples are rejected. Rhino owns sampling, persistence and transforms.

- `ScatterSampler.cs` — given boundary polygon(s), a pattern, a density spec, and a seed, returns 2D
  sample points. Patterns: Random / Grid / JitteredGrid / PoissonDisk (`ScatterPattern`). Density:
  Count / PerArea / Spacing (`ScatterDensityMode`), all reduced to one effective cell size. Deterministic
  SplitMix64 RNG (not `System.Random`) so results are stable across runtimes and rebuilds. Poisson
  occupancy is **sparse** (a cell-keyed dictionary, at most one entry per accepted sample) — a dense
  domain grid made the allocation a function of extent / spacing squared, unrelated to the requested
  cap, and its int product overflowed on a large region. Poisson cell keys retain full integer-valued
  double coordinates; beyond their exact range, spacing checks fall back to scanning accepted samples.
  Random counts are capped before integer conversion. Grid traversal checks cancellation within long
  rows, including rejected candidates. Every
  candidate is tested against per-loop bounding boxes before any polygon edge is walked.
- `ScatterRequest.cs` — the input bundle.
- `ScatterFootprint.cs` — conservative plan containment of block bounds, checking entire edges across
  concave notches and accepting boundary contact. A footprint must fit inside one region.
