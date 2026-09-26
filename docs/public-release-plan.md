# Public release plan

Everything to do before `rheinason/MoleHill` goes from private to public. Written 2026-09-26 from an
audit of the tree and the full git history. Delete this file once the repository is public and every
item is done or dropped (see `docs/cleanup-plan.md` → Doc lifecycle).

Status key: `[ ]` open · `[~]` in progress · `[x]` done · `[-]` dropped (say why).

## What the audit found clean

- No credentials in any of the 505 commits (token, key, password and private-key patterns).
- Every commit is authored by the owner; no third-party identities.
- Copied-case regression tests use local coordinates (all under 1 km), so they cannot locate a site.

## Decisions (owner, 2026-09-26)

- [x] **D1 Triangle licensing:** keep `src/TriangleNet/`. Carry Shewchuk's terms verbatim, add a GPL
  section 7 exception for combining MoleHill's GPL code with the TriangleNet subtree, and say plainly
  that selling MoleHill or including it in a commercial product needs Shewchuk's licence. Upstream, as
  checked 2026-09-26: Triangle "may not be sold or included in commercial products without a license"
  (cs.cmu.edu/~quake/triangle.html); Triangle.NET's author recommends against commercial use of the
  files carrying Shewchuk's header and publishes no NuGet package for that reason.
- [x] **D2 Office material:** owned by the office. Remove `Python Commands Source/` entirely, from the
  tree and from history. The toolbar icons rendered from its `Master.ai` go with it, including the
  copies embedded in `MoleHill.Toolbar.rui` since `7e42c49` (2026-09-11).
- [x] **D2a Commands with an office-script counterpart** (TwoPointInterpolation, GradientInterpolation,
  LiftCurvesWithLine, ReplaceCurveSection, SlopeCurve, SoftEditCurves, OffsetFeature, TrimBoundary,
  OrientToOrigin, SetSunNorth, ExternalizeBlock, UpdateAllLinkedBlocks, ImportGeoTiff,
  Add/RemoveMarkerParentheses): the owner's own C# reimplementation. They stay.
- [x] **D2b Icons:** every toolbar button redrawn procedurally in `generate-toolbar-icons.ps1`, same
  flat ink-and-accent style; the artboard renderer is deleted.
- [x] **D3 Data:** only the copied-case tests are published. Removed from tree and history:
  `120K Pointstest.csv`, `MediumSizedDataset.csv`, `GradePadTest.3dm`, `GradePadTest.3dmbak`,
  `CaseMeshResult.obj/.mtl`, and `.codex_tmp/` (a real case export, already gone from the tree).
- [x] **D4 Commit trailers:** strip the `claude.ai/code/session_…` lines from every commit message.
- [x] **Also removed from history (audit of files deleted long ago):** `build.binlog` (an MSBuild binary
  log records the build environment), `.dotnet/` and `.dotnet-cli/` (SDK state, including .NET
  `MachineId` telemetry files that identify the machine), and `artifacts/` (old build/verify output).
  Personal paths inside *old versions of docs* stay; only the tip tree must be free of them.

## Order of work

Tree fixes and new files first, as ordinary commits. The history rewrite comes last, so it also
cleans the commits made during this work, and it runs once, on a scratch clone, before a single
force-push.

### 1. Tree fixes

- [ ] **Office material out of the tree:** delete `Python Commands Source/` and
  `tools/render-toolbar-artboards.py`; remove the artboard mapping and the PNG-asset path from
  `generate-toolbar-icons.ps1`; update `CLAUDE.md`, `AGENTS.md`, `architecture.md` and `.gitignore`
  where they describe `Master.ai`.
- [ ] **Procedural icons:** draw a `$designs` entry for every command that used an artboard; delete
  `src/MoleHill.Rhino/Toolbars/icons/`; regenerate the `.rui` sprite strips; check the icons in Rhino.
- [ ] **Data out of the tree:** delete the D3 files; the benchmark and forensic tests that read them
  must still skip cleanly; fix docs that mention them.
- [ ] **Personal paths:** remove `C:\Users\hbxma\Dropbox\…` from `tests/perf-baselines/hosted-perf.json`
  (the lane should record a repo-relative path), `docs/rhino-live-testing.md`, and
  `docs/terrain-scalability-review-2026-09-09.md`.
- [ ] **Licence notices (D1):** Shewchuk's terms and the GPL section 7 exception in `LICENSE` and
  `LICENSES/TriangleNet.md`; BitMiracle LibTiff.NET listed beside Clipper2; the Yak package's
  `misc/licenses` in step; README licence section updated.

### 2. Public-facing files

- [ ] `CONTRIBUTING.md`: prerequisites (Windows, Rhino 8, .NET 8 SDK), build and test commands, what the
  validation lanes mean, native tests skip without Rhino, commit style.
- [ ] `SECURITY.md`: report privately through GitHub's private vulnerability reporting, not issues.
- [ ] `.github/ISSUE_TEMPLATE/` (bug report asking for Rhino/MoleHill versions and a repro case;
  feature request) and a pull request template.
- [ ] `CHANGELOG.md` for the Yak versions, starting from `0.14.6-beta`.
- [ ] CI: a GitHub Actions workflow on `windows-latest` that builds Core and runs `MoleHill.Core.Tests`
  (no Rhino needed).
- [ ] `AGENTS.md` / `CLAUDE.md`: mark machine-specific lines as such.
- [ ] README: status (beta), what it is, links to CONTRIBUTING and the licence notice.

### 3. History rewrite (one pass)

- [ ] Mirror-clone to a scratch directory. With `git filter-repo`: remove the D2/D3 paths and
  `src/MoleHill.Rhino/Toolbars/icons/`; replace every `.rui` blob that embeds artboard-derived bitmaps
  with the new procedural `.rui`; strip the D4 trailer lines.
- [ ] Verify on the clone: none of the removed paths, bitmaps or trailers in any commit; tip tree
  identical to local `main` apart from what was removed; builds; managed lane green.
- [ ] Owner approves the force-push.

### 4. Go public

- [ ] Force-push `main`; delete the merged `housekeeping-2026-09` branch on GitHub; replace the local
  clone with a fresh one (an old clone must never be pushed again).
- [ ] GitHub settings: description, topics, private vulnerability reporting on, wiki off if unused.
- [ ] Final check on a fresh clone: build, managed lane, no personal paths in the tree, nothing removed in
  `git log --all`.
- [ ] Owner flips visibility to public.
- [ ] Delete this plan.

## Log

- 2026-09-26: plan written from the audit; decisions D1–D4, D2a, D2b taken.
