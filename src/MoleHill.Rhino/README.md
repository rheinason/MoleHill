# MoleHill.Rhino

The native Rhino plugin (`.rhp`): the dockable panel, the commands, the terrain definition model, and
the controller/build-service pipeline that turns a terrain definition into geometry in a document.

All Rhino API calls belong here (or in `MoleHill.Grasshopper`); reusable computation belongs in
`MoleHill.Core`. The rule is load-bearing rather than stylistic — Core is what the test suite can
exercise without a host.

## Folders

| Folder | What lives there |
|---|---|
| [`Model/`](Model/README.md) | The persisted terrain definition: modifiers, zones, markers, objects, analyses, annotations |
| [`Registry/`](Registry/README.md) | Type descriptors and the parameter schema that drives every card |
| [`Services/`](Services/README.md) | `TerrainController` / `TerrainBuildService`, the runtime cache, output and layer routing |
| [`UI/`](UI/README.md) | The Eto.Forms panel, split across partials, and its reusable card/editor primitives |
| `Commands/` | `MoleHillPanel`, `MoleHillCreateTerrain`, `MoleHillConvertToRhino` and the command services |
| `Toolbars/` | The `.rui` and the committed hand-drawn icon PNGs it is packed from |
| `Resources/` | 16x16 panel tab and modifier badge icons, loaded via `PanelIcons.Load()` |

## Before changing the build pipeline

- [docs/build-result-ownership.md](../../docs/build-result-ownership.md) — who owns which geometry, and
  which disposal paths currently have no endpoint. A worker cache borrows most of what it holds.
- [docs/architecture.md](../../docs/architecture.md) — output layer roles, preview vs bake, analysis vs
  annotation, document schema compatibility.
- [docs/rhino-live-testing.md](../../docs/rhino-live-testing.md) — how to test the panel for real. Close
  Rhino before rebuilding: it locks the `.rhp`.

State is persisted as JSON in the `.3dm` via WriteDocument/ReadDocument. A document written by a newer
build is refused rather than silently rewritten — see the schema-compatibility section of the
architecture doc.
