# MoleHill release review — 2026-09-16

## Verified

- Version source: `Directory.Build.props` → `0.14.3-beta`.
- Solution build: `dotnet build MoleHill.sln --no-restore -p:SkipGrasshopperLibraryCopy=True -v:q` (0 errors).
- Core tests: 858 passed, 0 skipped.
- Rhino tests: 639 passed, 113 native skips.
- Grasshopper tests: 36 passed, 15 native skips.
- Grasshopper library: live Rhino 8 discovery found all 18 MoleHill components.
- Live canvas smoke: migrated components placed and solved with expected missing-input diagnostics and no runtime exceptions.
- Revit adapter syntax: all three Python adapters compile successfully.
- Yak package: `.artifacts/yak/MoleHill-0.14.3-beta/molehill-0.14.3-beta-rh8_9-win.yak` built successfully.
- Shipped examples: Snapshot branch graph and live-saved modifier inventory graph are present.

## Host-dependent follow-up

- Normal `GH_DocumentIO.Open` remains running beyond a ten-second bounded probe; direct `GH_Archive` +
  `GH_Document.Read` migration remains verified.
- No Revit or Rhino.Inside.Revit installation is present on this machine, so transaction execution and
  configured Revit `.gh` examples require a Revit host.
- Native modifier comparison fixtures remain a release-review follow-up; Core behavior and live component
  registration are covered.
