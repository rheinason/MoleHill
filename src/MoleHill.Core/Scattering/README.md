# MoleHill.Core/Scattering

Object-scatter point distribution (the Scatter object type). Pure, unit-tested; the Rhino side
(`TerrainBuildService.Scatter.cs`) projects the returned points onto the terrain and emits block
instances.

- `ScatterSampler.cs` — given boundary polygon(s), a pattern, a density spec, and a seed, returns 2D
  sample points. Patterns: Random / Grid / JitteredGrid / PoissonDisk (`ScatterPattern`). Density:
  Count / PerArea / Spacing (`ScatterDensityMode`), all reduced to one effective cell size. Deterministic
  SplitMix64 RNG (not `System.Random`) so results are stable across runtimes and rebuilds.
- `ScatterRequest.cs` — the input bundle.
