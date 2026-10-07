# MoleHill codebase review — 2026-10-07

Reviewed revision: `d3f65b1a63d7b9824c609afffe46c97ab6d46192` (`1.3.3-beta`). The working tree was clean at the start. This continues the September architecture review and October release review, checking their conclusions against current sources.

This delivery is a review and fix plan. Production code is unchanged. Proposed fixes below are not implemented by this report.

## Assessment

| Question | Assessment |
|---|---|
| Is it modular? | **Yes at project and feature boundaries; partially within the Rhino host.** Core is independent of Rhino/GH, Revit is isolated, and descriptors give features a clear extension path. The controller and build context still concentrate substantial mutable state. |
| Is it good for collaboration? | **Good foundations for a small team, with integration bottlenecks.** Contribution guidance, issue/PR templates, conventions, source navigation and architectural guard tests help. Host changes lack automatic PR validation, and contradictory documentation can misdirect a new contributor. |
| Is it ready for real work? | **A credible candidate for controlled Rhino/GH production use, subject to host acceptance; not a blanket release sign-off.** Current managed tests, warning checks and packaging pass. The Revit writer has concrete correctness risks and explicitly lacks host verification. |
| Does it follow best practices? | **Often, especially for domain invariants and regression testing.** Cache identity, schema compatibility, shared conversions, output routing and measured performance lanes are strong. Transaction outcomes, host integration coverage and scheduler testability need further work. |

Preserve the architecture and improve specific boundaries. A rewrite, more assemblies everywhere, or blanket disposal of Rhino geometry would introduce risk without addressing the main findings.

## Scope and evidence

Reviewed project references, CI, contribution/build guidance, packaging and validation scripts, registries, stage dispatch, cache ownership, controller scheduling/publication, serialization, representative regression tests, and the Revit writer/planner/input boundary. This is a risk-oriented review, not a line-by-line audit of every algorithm or a measured coverage assessment.

Source inventory, excluding `bin`/`obj`, including comments and blank lines:

| Area | C# files | Lines |
|---|---:|---:|
| Core | 217 | 59,872 |
| Rhino | 383 | 71,714 |
| Grasshopper | 38 | 6,922 |
| Shared | 9 | 4,469 |
| Interop | 5 | 119 |
| Revit | 11 | 1,042 |
| Vendored TriangleNet | 82 | 25,276 |
| Three test projects combined | 358 | 342,634 |

Size identifies review pressure, not defects. Much of the test volume is captured geometry data.

### Validation performed on this revision

SDK: `10.0.400`. Commands were run from the repository root.

| Command | Result | Evidence directory under `.artifacts/validate/` |
|---|---|---|
| `pwsh -NoProfile -File ./validate.ps1 managed` | Pass after retry: Core **1,345 passed**; Rhino **1,212 passed / 148 skipped**; GH **56 passed / 18 skipped**. Total **2,613 passed / 166 skipped**. Clean build: zero warnings/errors. | `managed-20261007-230425/` |
| `pwsh -NoProfile -File ./validate.ps1 warnings` | Pass; script reports 10 compiled outputs and zero owned-code warnings. | `warnings-20261007-230554/` |
| `pwsh -NoProfile -File ./validate.ps1 package` | Pass; 21 archive entries, including `.rhp`, both `.gha` files, Core, Interop and manifest. Nothing published. | `package-20261007-230640/` |
| `pwsh -NoProfile -File ./validate.ps1 hosted-perf -HostedResult .artifacts/review-2026-10-07-hosted-gate-fixture.json` | **Synthetic gate probe**, not a benchmark: accepted zero samples, a missing metric and changed output, with exit 0. See F08. | `hosted-perf-20261007-230813/` |

The first managed attempt failed during NuGet restore because the sandbox blocked network access (`NU1301`/`NU1900`); the permitted retry completed. That initial failure is environmental, not a code defect.

No native suite, interactive UI test, installed-package load, real hosted performance run or Revit transaction was executed in this review. Historical live evidence is credited below, but does not substitute for testing the current packaged revision. Benchmark bodies that opt out can appear among managed passes; these counts establish no performance claim. Local `.artifacts` evidence is not committed with this report.

## What is working well

- **Real host separation.** [Core](../src/MoleHill.Core/MoleHill.Core.csproj) has no Rhino/GH/Eto reference or corresponding `using` directives in its source. Hosts depend on Core and the small [Interop contract](../src/MoleHill.Interop/README.md). RevitAPI is confined to the Revit project. Shared Rhino conversions have a deliberate home rather than independent host implementations.
- **Features have an extension mechanism.** [TerrainTypeRegistry](../src/MoleHill.Rhino/Registry/TerrainTypeRegistry.cs) discovers descriptors, which connect construction, parameters, serialization identity and stage dispatch. Separate analysis/annotation families share typed parameter infrastructure. This reduces the number of central switches a contributor must edit.
- **Domain rules are executable.** Packed-edge comparer, vocabulary, parameter schema, registry and schema-version guard tests protect expensive lessons. Future-schema refusal, geometric regression fixtures and cache-key conventions are more valuable here than a superficial pursuit of small files or generic abstractions.
- **Ownership has progressed.** `CreateWorkerCopy` records borrowed meshes, `DiscardOwnedMeshOutputs` rejects main-cache misuse, and completion/retirement paths discard worker-owned stage meshes. Display geometry deliberately follows GC ownership because of external readers. The October 5 notes record two native edit soaks without a growing retained-memory trend. This is historical evidence for those fixtures, not proof for every workload.
- **Packaging has substantive checks.** The one packaging script builds isolated outputs, hashes duplicate dependencies, checks the archive contents and guards staging cleanup. This review exercised that path successfully.
- **Collaboration infrastructure exists.** `CONTRIBUTING.md`, issue templates, a PR template, `.editorconfig`, folder READMEs, file indexing and detailed conventions give contributors a usable starting point. The codebase does not need a new process framework before useful collaboration can start.

## Prioritized findings

P1 means address before relying on the affected workflow or making a broad release-readiness claim. P2 means important maintainability or assurance work. P3 means worthwhile maintenance when touching the area. Validation gaps are explicitly distinguished from runtime defects.

| ID | Priority | Kind | Finding |
|---|---|---|---|
| F01 | P1 | Correctness | Revit writer ignores transaction completion status |
| F02 | P1 | Data preservation | Host replacement deletes omitted subdivisions |
| F03 | P2 | Correctness | Revit input conversion discards document identity |
| F04 | P1 | Integration assurance | PR CI checks Core, leaving host changes outside the automatic gate |
| F05 | P1 | Release assurance | Archive validation does not establish installed host compatibility |
| F06 | P2 | Modularity/testability | Scheduler behavior remains embedded in the controller and excluded from source-linked tests |
| F07 | P2 | Modularity | Extracted stages still receive broad mutable build state |
| F08 | P2 | Validation | Imported performance results can succeed without sufficient evidence |
| F09 | P2 | Collaboration | Architecture and ownership guidance contradict newer code and notes |
| F10 | P3 | Collaboration | Large geometry fixtures obscure the test logic in C# source |

### F01 — Require successful Revit transaction completion

**Evidence:** [ToposolidWriter.cs](../src/MoleHill.Revit/RevitHost/ToposolidWriter.cs), lines 56–94. The writer populates its output/report lists, calls `transaction.Commit()` and `group.Assimilate()`, ignores their returned statuses, and returns the result. The exception handler covers thrown failures only.

**Trigger and consequence:** Revit failure handling can return `RolledBack` from `Commit()` without throwing. The writer can consequently expose created/replaced messages and element handles for changes that were not committed. A `Pending` result also needs its own lifecycle; it must not be treated as a completed transaction. These API outcomes are documented by [Autodesk](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/32714010-7138-f64f-8fde-a310354448e3.htm). The source omission is confirmed; this review did not reproduce it inside Revit.

**Fix:** Check start, commit and assimilation outcomes. Publish results only after successful completion. Handle rollback with an explicit failure result and no success outputs. Define a supported failure-handling policy for pending operations; do not blindly assimilate or roll back while Revit is still processing failures. Preserve the original error if cleanup also fails.

**Acceptance:** In Revit, force commit-time rollback using failure handling; assert no created elements, no success output and unchanged originals. Cover successful commit/one-step Undo and the chosen pending/failure policy. Planner-only tests cannot verify this.

### F02 — Preserve omitted subdivisions during host replacement

**Evidence:** [ToposolidWriter.cs](../src/MoleHill.Revit/RevitHost/ToposolidWriter.cs), lines 64–79 and 170–186. A changed host is replaced, only current plan subdivisions are written, and the predecessor is deleted. `ReportOrphanedSubdivisions` checks `HostTopoId == host.Id`, where `host` is already the replacement, so omitted subdivisions on the predecessor are neither preserved nor reported there. The [README](../src/MoleHill.Revit/README.md) promises that removed zones are reported and left in place.

**Trigger and consequence:** Start with a host containing zones A and B. Change the host geometry and submit only A. B remains attached to the old host and is deleted with it. With subdivision writing disabled, replacing the host can remove all its existing subdivisions. Transaction atomicity does not protect against a successful but unintended deletion. [Autodesk documents that deleting an element also deletes dependent elements](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/a0461dd1-71d9-4581-1604-2ef8c211dd60.htm).

**Fix:** Inventory the predecessor's subdivisions and other affected dependents before replacement. Preserve supported content explicitly. If the writer cannot preserve omitted or user-authored dependents, refuse that replacement with an actionable report before committing. Inspect the deletion set and roll back unexpected removals. Reconcile the documentation with the actual preservation policy.

**Acceptance:** Native cases for A+B → A with changed host geometry; subdivision writing off; a user-created subdivision; and a dependent the writer cannot preserve. The result must preserve content or refuse the whole operation. Verify Undo restores the exact predecessor state.

### F03 — Validate Revit input document and element type before reducing to an ID

**Evidence:** [RevitInputs.cs](../src/MoleHill.Revit/RevitHost/RevitInputs.cs), lines 32–56, converts an `Element` straight to `element.Id`. [RevitSession.cs](../src/MoleHill.Revit/RevitHost/RevitSession.cs), lines 49–57, passes the converted type, level and subdivision-type IDs to the active document without checking the source element's document.

**Trigger and consequence:** A Grasshopper input retains an element from document A while document B becomes active. Its numeric ID may resolve to an unrelated element in B, producing an error or, if it names a suitable type/level, using an unintended one. Element IDs are only unique within one project, as [Autodesk specifies](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/44f3f7b1-3229-3404-93c9-dc5e70337dd6.htm). This is a source-level finding, not a native reproduction.

**Fix:** Resolve typed inputs against the target document while provenance is still available. Reject foreign-document or invalid elements. Resolve bare IDs only within the explicitly selected document and check the expected API type. Report the offending input before opening the write transaction.

**Acceptance:** Two-document tests with colliding IDs, wrong element types, invalidated elements and valid same-document inputs. Confirm failures leave both documents unchanged.

### F04 — Put host changes behind an automatic integration gate

**Evidence:** [.github/workflows/ci.yml](../.github/workflows/ci.yml) builds and tests only `MoleHill.Core.Tests`. Many registry, serializer, layer-role, unit and ownership tests are in `MoleHill.Rhino.Tests`, and GH/Revit planning tests are in `MoleHill.Grasshopper.Tests`. This review's local success does not change what CI covers.

**Consequence:** A PR can break a host build, a persisted definition or component behavior while the configured CI remains green. This weakens independent collaboration even when contributors correctly follow the local instructions. Repository files do not establish whether external branch protections or private CI exist; those settings were not inspected.

**Fix:** Keep fast Core CI. Add a reproducible Windows host build and managed host-test lane with declared reference inputs. Where installed host dependencies are necessary, use a controlled runner or a maintainer-triggered job for reviewed commits. Do not execute untrusted PR code on a privileged maintainer machine. Publish TRX, warning and skip counts as artifacts, and require the relevant check before merging host changes.

**Acceptance:** A deliberate host compilation error and a failing host guard test each fail the check. A Core-only contribution can still validate without Rhino. Document which checks branch protection actually requires.

### F05 — Add installed-package acceptance to release evidence

**Evidence:** [validate.ps1](../validate.ps1), the package lane, checks archive contents. [The lane documentation](validation-lanes.md) correctly states that source-linked tests do not verify shipped assembly loading. The [Revit README](../src/MoleHill.Revit/README.md) says its runtime writer has never run in Revit, and that the included net8 assembly produces a loading error in plain Rhino forced to .NET 7.

**Consequence:** A complete archive can still have discovery, runtime binding, UI or transaction defects. A Rhino 8.9 reference floor is not evidence that every packaged assembly loads under every advertised Rhino runtime. This review does not prove that the current Rhino/GH product is broken; it establishes that the checks performed cannot certify it.

**Fix:** Produce a versioned release acceptance record containing commit, archive SHA-256, Rhino/runtime/GH versions and results from loading that exact archive in an isolated host. Cover terrain create/edit, cancel/settle, save/reopen, Undo, preview/bake and GH component discovery/snapshot exchange. State the supported runtime matrix, including the optional Revit assembly's behavior on .NET 7. Mark the Revit writer experimental until F01–F03 and real Revit acceptance are complete.

**Acceptance:** Repeatable disposable-host smoke scripts with explicit assertions and cleanup; a current artifact load record; Revit create/unchanged/replace/rollback/Undo tests for each claimed supported host family. Package contents and screenshots alone are insufficient.

### F06 — Extract scheduler decisions from the host controller

**Evidence:** `TerrainController*.cs` spans 20 files / 6,154 lines; [TerrainController.Build.cs](../src/MoleHill.Rhino/Services/Controller/TerrainController.Build.cs) alone is 1,005 lines. Scheduling combines clocks, timers, document events, cancellation, generation/version checks, publication and persistence across the partials. [The Rhino test project](../tests/MoleHill.Rhino.Tests/MoleHill.Rhino.Tests.csproj), line 58, excludes `TerrainController*.cs`. Small debounce/supersession policies are tested, but that does not exercise the integrated controller state machine. The October 5 ownership notes record a real stranded-request bug found by the live soak.

**Consequence:** Partial files reduce edit conflicts but share one class's private state. Contributors changing scheduling, document lifetime and display publication still need to reason about one coupled system; ordinary source-linked tests cannot protect its event sequences.

**Fix:** Incrementally extract a scheduler state object driven by explicit request/completion/reset/close events, with injected time. Have it return decisions such as dispatch, cancel, publish-preview, publish-final and discard. Keep Rhino calls and geometry retirement in a thin host adapter. Preserve current borrowed/cache/display ownership rules; scheduler extraction must not add disposal of displayed geometry.

**Acceptance:** Deterministic sequence tests for rapid edits, an overdue request after cancellation, out-of-order completion, stale generations, reset/delete/close, and superseded preview followed by final publication. Then replay those workflows in Rhino. Move one transition at a time, rather than rewriting the controller.

### F07 — Narrow stage inputs and outputs after the successful stage extraction

**Evidence:** Modifier stages already have separate classes in `Services/Build/Stages/`; the September claim that this extraction is unstarted is obsolete. However, [ModifierBuildContext](../src/MoleHill.Rhino/Registry/ModifierBuildContext.cs), lines 13–31, supplies the entire definition, snapshot, result, runtime cache and mutable mesh/fingerprint state. [SmoothStage](../src/MoleHill.Rhino/Services/Build/Stages/SmoothStage.cs) reaches many `TerrainBuildService` helpers. The build-service partial family remains 22 files / 7,331 lines.

**Consequence:** Features can be edited separately, but a stage can still mutate unrelated cache/result state or change a mesh without keeping its fingerprint consistent. This is a boundary weakness, not proof of a current geometry defect.

**Fix:** Pilot a narrow request/result contract on one stage. Bundle mesh and fingerprint together; expose only required snapshot data, cache operations, cancellation and diagnostics. Have the orchestrator apply the returned result. Retain shared implementations for tolerance, conversion and cache semantics; do not clone helpers or introduce an interface for every function.

**Acceptance:** Unchanged cold/cache-hit output, constraint and cancellation behavior; direct stage tests that need less whole-terrain setup; fewer reasons for unrelated stages to access shared mutable state. Expand only if the pilot makes changes safer and easier to review.

### F08 — Make imported performance evidence fail when incomplete

**Evidence:** [validate.ps1](../validate.ps1), lines 342–425, prints imported sample/environment data but does not validate positive sample counts or require measurements. `Missing` metrics and `OutputChanges` are reported; only `Regressed` metrics fail the comparison. The imported `Comparison` object is trusted rather than recomputed against the supplied current baseline.

**Reproduction performed:** A synthetic JSON with `SamplesPerScenario = 0`, empty measurements, one `Missing` comparison metric and one changed output was supplied through `-HostedResult`. The lane exited 0 and printed “No regression beyond the margin.” This tests the result-validation boundary only; no Rhino was started and no timing was measured.

**Consequence:** A malformed, stale or incomplete imported result can look like acceptance. Even legitimate stage renames can silently remove comparisons. Output changes can represent intended features, so they should be an explicit review decision rather than being indiscriminately forbidden.

**Fix:** Validate the result schema, positive sample counts, required scenarios/phases, optimized-build evidence and minimum metric set. Recompute comparisons against the intended baseline, or verify its identity/hash and comparison provenance. Add a strict acceptance mode that fails missing required metrics and unexpected geometry changes. Permit declared scope/metric migrations and intentional output changes only through an explicit baseline update with a reason.

**Acceptance:** Small script-level fixtures for zero samples, missing wall metrics, missing phases, obsolete baseline identity, changed geometry, declared partial runs and valid comparisons. Invalid evidence must fail before any success message. These are gate tests, not performance tests.

### F09 — Replace conflicting historical guidance with a current contract

**Evidence:** [architecture.md](architecture.md), lines 1704–1715, says worker ownership is not distinguished and calls the ownership work characterization only. Current `TerrainRuntimeCache` tracks borrowed meshes. [build-result-ownership.md](build-result-ownership.md) opens with “Nothing ... changed in code,” but later documents implemented cleanup, native soaks and intentional GC ownership. Its tail still discusses a possible ownership object. The September review contains similarly outdated unstarted-work tables. The architecture “one-page map” is now 1,803 lines.

**Consequence:** A contributor following the introductory guidance could reintroduce unnecessary ownership machinery or dispose geometry still read by rendering/GH. This is more serious than document length: incompatible directions coexist in the same navigation path.

**Fix:** Put the current ownership rules and acceptance status first, remove superseded prescriptions, and keep history clearly labeled or in Git. Update the architecture ownership section and folder README links together. Turn the September plan into a short list of work genuinely still open; retain useful completed-work explanations in reference docs. Keep the architecture entry page a concise map with links to detailed subsystem references.

**Acceptance:** A reader can identify the current owner and cleanup policy for every geometry family without reconciling dates. No active plan calls an implemented feature unstarted. In particular: borrowed stage meshes are protected, worker-owned stage meshes are discarded, and anything reachable from display state is not explicitly disposed.

### F10 — Move captured geometry out of test method bodies gradually

**Evidence:** [GradePathDistantHardConstraintCopiedCaseTests.cs](../tests/MoleHill.Core.Tests/GradePathDistantHardConstraintCopiedCaseTests.cs) contains 79,818 lines; `GradePathRetainingWallRegressionTests.cs` contains 39,059. They preserve valuable production regressions, but most of their source volume is numeric fixture data.

**Consequence:** Reviewers must navigate large generated diffs to find assertions, and fixture edits create noisy merges. This review did not measure compiler or test-loader cost, so no performance saving is claimed.

**Fix:** Pilot one fixture as a versioned embedded data resource with provenance, units, counts and a content hash, retaining the small test and its meaningful geometry assertions in C#. Preserve double values exactly and validate lengths/indices when loading. Keep the current reproducer until byte/value equivalence is established. Do not replace strong production cases with tiny synthetic tests just to reduce file size.

**Acceptance:** Identical input arrays and regression outcome, deterministic resource loading, readable diffs, and measured build/load cost before claiming any speed benefit.

## Changes since the earlier review

| Earlier concern | Current disposition |
|---|---|
| Future schema may be rewritten lossily | Guard and regression tests exist. The native store-level test remains outside this review's executed coverage. |
| Packed-edge hashing | Factories/comparers and a source guard exist; current Core suite passes. |
| Build paths and warning suppression | Declared host inputs and a working solution-wide warning ratchet exist; current warning lane passes. |
| Unmerged worker stage meshes | Borrowed/owned tracking and discard paths are implemented. Do not repeat the old “nothing disposes them” finding. |
| Display/generated geometry lifetime | October 5 notes adopt GC ownership with bounded behavior on two recorded soak fixtures. Preserve that policy; do not infer a permanent leak from missing `Dispose()`. |
| Separate modifier stage classes | Implemented. F07 concerns the remaining breadth of their contracts. |
| Integrated scheduler tests | Still an open boundary problem; F06. |
| Historical native/performance gaps | Some subsequent live measurements exist. This review adds no new native or timing evidence. |

## Recommended delivery order

1. **Revit correctness:** F01–F03 as focused changes, with a native writer acceptance fixture. Keep the integration's experimental status explicit until those checks pass.
2. **Release and collaboration gates:** F04–F05. Record evidence for the exact commit/archive and make the host checks repeatable. A release record should identify skipped and unverified work, not only successful totals.
3. **Small assurance/documentation fixes:** F08–F09. Harden the imported-result gate and reconcile current ownership guidance before another scheduler refactor.
4. **Scheduler extraction:** F06, preserving behavior and ownership through deterministic sequence tests plus native replay.
5. **Incremental maintainability:** one stage-contract pilot for F07 and one fixture pilot for F10. Avoid combining these with geometry behavior changes.

For collaboration, divide work by Core algorithm, host adapter, descriptor/stage, or validation tooling. Changes spanning scheduler, cache lifetime and publication should have one coherent owner/reviewer even when spread across partial files. Add an ownership/review map when multiple maintainers can actually honor it; an empty or nominal `CODEOWNERS` file adds little.

For reproducibility, retain the declared SDK floor and supported plugin TFMs. The combination of `latestMajor` and `LangVersion=latest` makes the tested compiler relevant: add an SDK-floor build alongside the maintainer SDK, and record the release SDK and resolved dependency graph. Consider lock files with locked restore for release builds. A newer SDK alone is not a reason to retarget the plugins.

## Readiness decision

The current code has enough structure, regression coverage and operational tooling to support serious continued development and controlled Rhino/GH project use. The limiting factors are host-level assurance and a few concentrated stateful boundaries, not the absence of modular design. Broad production certification needs installed-package evidence; Revit writing additionally needs the concrete correctness fixes above. No performance improvement or native acceptance is claimed by this review.
