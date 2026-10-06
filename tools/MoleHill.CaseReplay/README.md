# MoleHill.CaseReplay

Replays a case bundle (the panel's **Copy Case** zip) without an interactive Rhino. Rhino runs in-process and
headless through Rhino.Inside; the bundle's `sources.3dm` goes into a headless document on the layers each
object was resolved from (block definitions included), and the terrain in `terrain.json` is built through the
plugin's own `TerrainBuildSnapshotBuilder` and `TerrainBuildService`, exactly as the panel builds it.

```bash
dotnet build MoleHill.sln -c Release                       # the plugin the tool loads
dotnet build tools/MoleHill.CaseReplay
DOTNET_ROLL_FORWARD=Major tools/MoleHill.CaseReplay/bin/Debug/net7.0-windows/MoleHill.CaseReplay.exe <case.zip|dir> [--stages]
```

- `--stages` also builds after each modifier and prints its mesh: vertices, faces, border loops,
  non-manifold edges. One border loop and zero non-manifold edges after every stage is a healthy terrain.
- `--plugin <dir>` loads a different plugin build (default `src\MoleHill.Rhino\bin\Release\net7.0`).
- `--units <UnitSystem>` sets the document units. Bundles record them since 2026-10-06; older bundles
  default to Meters. (Older bundles also tag `sources.3dm` as millimetres whatever the model's units, so
  importing one into Rhino by hand scales it by the unit ratio; this tool reads coordinates as stored.)

Run from the repository root, with no Rhino holding the plugin's Release DLLs. A Rhino licence is required.
