# Rhino live-testing methodology

The repeatable procedure for testing Rhino-hosted UI and commands such as `mhInspectCurve`.

**Drive Rhino through the `rhino-mcp` router, not through synthetic keystrokes.** The router
(`mcp__rhino-mcp__*`) spawns a disposable Rhino, runs commands and RhinoCommon scripts inside it, and
returns their output as data. That gives deterministic, inspectable results and never touches the
user's own Rhino session. `WScript.Shell`/`SendKeys` and `user32` cursor calls are a fallback for
genuine pointer gestures only — they are blind, focus-dependent, and cannot report what happened.

## 1. Build before launching

Build with no Rhino holding the `.rhp`:

```powershell
dotnet build src\MoleHill.Rhino\MoleHill.Rhino.csproj --no-restore
```

`MSB3021`/`MSB3027` are **errors, not warnings** — a running Rhino holds `MoleHill.Core.dll` and
`MoleHill.Rhino.rhp`, the copy fails, and the build fails with it. Nothing is produced, so there is no
question of testing a stale binary; the build simply does not happen. Close the locking Rhino (see §2 for
how to close one the router will not) and build again.
Plugin path: `src\MoleHill.Rhino\bin\Debug\net7.0\MoleHill.Rhino.rhp`.

**So close the slot before every rebuild.** A live test cycle is spawn → test → `close_slot` → edit →
build → spawn again, and skipping the close is the most common way to lose ten minutes here.

**A green test run is not a compiling plugin.** `MoleHill.Rhino.Tests` *links* `Model/`, `Registry/` and
most of `Services/` as source and project-references only `MoleHill.Core` — it never builds
`MoleHill.Rhino.csproj`. So `UI/` is outside every test build, and a compile error there (a duplicated
helper, say) passes the whole suite and surfaces only when the plugin itself is built. If a lock is
blocking the real output, compile to a scratch directory rather than assuming:

```powershell
dotnet build src\MoleHill.Rhino\MoleHill.Rhino.csproj -p:OutDir=$env:TEMP\verifybuild\
```

## 2. Spawn a disposable slot

```
spawn_slot(version: "8")   →   { slotId, port, pid, adopted }
```

Record `slotId` and `pid`; pass `slot` explicitly on every later call. `close_slot` normally kills
exactly that instance.

**`adopted` does not mean "the user started it".** It means "this router session did not spawn it", and a
Rhino the router spawned earlier can become adopted — after the router loses track of it (see §7 on
`_-Open`), or across router restarts. `close_slot` then refuses it with `cannot_close_adopted` and there
is **no router-side way to close it**, so it sits holding the build lock from §1 for the rest of the
session. Verified 2026-09-13: two Rhinos both launched with `/runscript="_MCPSpawn"` — router-spawned,
neither user-started — were both reported `adopted: true`, and both refused.

That command line is how to tell them apart, and it is the only reliable way:

```powershell
Get-CimInstance Win32_Process -Filter "Name = 'Rhino.exe'" |
    Select-Object ProcessId, CreationDate, CommandLine
```

A router-spawned Rhino carries `/runscript="_MCPSpawn"`; the user's own does not. Stop **that exact
PID**, and only after reading its command line — never `Stop-Process -Name Rhino`, which would take the
user's unrelated session with it. If the environment blocks stopping the process, say so and ask the user
to close the window rather than leaving the build silently broken.

Confirm the slot is alive and the script host works before doing anything else:

```
run_csharp(slot, "Console.WriteLine(__rhino_doc__.Objects.Count);")
```

## 3. Load the plugin and confirm it really loaded

First check whether the exact build is already loaded. MoleHill loads at startup; calling the path
overload again can open a modal "ID already in use" error even when the expected assembly is active.
Use `PlugIn.Find` with MoleHill's ID and verify the instance's assembly path. Only load by path when
there is no loaded instance — **not** via `_PlugInManager`, which opens a modal dialog:

```csharp
var id = new System.Guid("0c0b9e83-4959-437e-a935-4addf0d3f886");
string expected = @"C:\Users\hbxma\Dropbox\TopoTest\src\MoleHill.Rhino\bin\Debug\net7.0\MoleHill.Rhino.rhp";
var plugin = Rhino.PlugIns.PlugIn.Find(id);
if (plugin == null)
{
    var result = Rhino.PlugIns.PlugIn.LoadPlugIn(expected, out id);
    Console.WriteLine("result=" + result);
    plugin = Rhino.PlugIns.PlugIn.Find(id);
}
if (plugin == null || !string.Equals(plugin.GetType().Assembly.Location, expected,
    System.StringComparison.OrdinalIgnoreCase))
    throw new System.Exception("The exact plugin build is not loaded.");
Console.WriteLine(plugin.GetType().Assembly.Location);
Console.WriteLine(plugin.GetType().Assembly.ManifestModule.ModuleVersionId);
```

Then verify two things, because `LoadPlugIn` reports `Success` for an already-registered plugin
whether or not this session actually loaded it:

- `get_commands(filter: "mh")` lists the MoleHill commands. Note that this also lists commands from
  *registered but unloaded* plugins, so it is necessary, not sufficient.
- Read the command history for a load refusal:

  ```csharp
  Console.WriteLine(Rhino.RhinoApp.CommandHistoryWindowText);
  ```

  A `Blocking plug-in MoleHill.Rhino.` line requires checking the loaded instance, not assuming success
  or failure from history alone. In the 2026-09-10 smoke run, that startup line coexisted with a loaded
  instance at the exact build path. If the instance is absent or points elsewhere, the build remains
  unverified. Inspect registration and Mark of the Web (`Get-Item <file> -Stream Zone.Identifier`)
  before choosing a repair; never test another installed version as though it were the new build.

## 4. Build the scene and drive the command as data

Create known geometry and set the selection from script, so the test states its own preconditions:

```csharp
var pts = new System.Collections.Generic.List<Rhino.Geometry.Point3d>();
for (int i = 0; i <= 20; i++)
{
    double x = i * 5.0;
    pts.Add(new Rhino.Geometry.Point3d(x, 0, System.Math.Sin(x * 0.05) * 6.0));
}
var gid = __rhino_doc__.Objects.AddCurve(Rhino.Geometry.Curve.CreateInterpolatedCurve(pts, 3));
__rhino_doc__.Objects.Select(gid);
__rhino_doc__.Views.Redraw();
Console.WriteLine(gid);
```

Then `run_command(slot, "mhInspectCurve")`. **Before running any command that picks objects, read its
`GetObject` setup.** `mhInspectCurve` calls `EnablePreSelect(true, true)`, so a pre-selected curve is
consumed immediately and the call returns `Done.`. A command that does not pre-select will sit at its
prompt, block the tool call for 120 s, and leave the slot wedged.

Query results with `list_objects` / `get_context` / `run_csharp` rather than inferring them from a
screenshot.

## 5. Visual verification

`get_viewport_image` captures the **viewport only** — meshes, curves and display colour — and its
metadata block (camera, framed bounds, on-screen object count) diagnoses an empty capture without
re-shooting.

**It is not usable for conduit-only output.** The emptiness guard counts *document objects*, not what is
on screen: with a terrain preview conduit actively drawing and every document object hidden, the call
returns `"Viewport is empty — no document objects intersect the view frustum"` and **no image at all**
(verified 2026-09-13 — `totalObjectCount: 0`, while the same build reported `1 preview outputs`). To
capture preview output, bake it first or leave a document object in frame; and note that baking a terrain
with a preview-colouring analysis active bakes the per-face colour mesh (three vertices per face), not
the welded TIN.

Keep the image small. A 640×640 capture came back as ~254k characters, over the tool-result limit, and
was spilled to a file; 220×220 distinguishes a populated viewport from an empty one, and the metadata
answers most questions without an image at all.

Eto panels and modeless forms are separate top-level windows and are *not* in that image. Capture
them from their real window bounds — never from guessed desktop coordinates:

```powershell
# EnumWindows filtered to the slot PID → GetWindowText + GetWindowRect
# then System.Drawing CopyFromScreen over that rect → PNG → read the PNG
```

The window rect is authoritative evidence that the form exists, where it is and how large it is, and
resize behaviour can be checked by re-reading the rect after a resize. `CopyFromScreen` reads the
physical screen, so the window must be unobscured on an unlocked session; if the capture comes back
blank, treat that as *unresolved* — confirm through a second channel (walk the control tree via
`run_csharp`, or ask for manual inspection) before reporting either success or a paint bug.

## 6. Acceptance sequence for the curve-review workflow

1. `mhInspectCurve` opens the form and Rhino stays responsive (`get_context` still answers).
2. The form window exists at sane bounds; resize and re-read the rect.
3. Select a station range, use `Offset`/`Grade`, then `Smooth` or `Soft Move`. Confirm the profile
   preview changes and plan XY geometry does not (compare `Curve.PointAt` X/Y before and after).
4. Apply once, then Rhino `_Undo` once; confirm the source curve returns to its prior profile.
5. Pick a second curve and confirm the same form retargets (the `SetObject` path, not a rebuild).
6. Close the form; confirm the conduit, timer and provisional session are torn down.

## 7. Known failure modes

- **`spawn_slot` failing with Windows error 5 ("breakaway not permitted") is the host, not this
  procedure.** The router launches Rhino with `CREATE_BREAKAWAY_FROM_JOB` so the new process outlives
  the tool call. Whether that is allowed is decided by the Job Object the router itself was created in,
  and the router is a child of whichever agent host started it — each host spawns **its own** instance
  (measured 2026-09-14: two routers parented to `claude.exe`, eight to one `codex.exe`, all in jobs).
  A host that creates its children in a job without `JOB_OBJECT_LIMIT_BREAKAWAY_OK` — typical of a
  sandboxed runner, which wants killing the agent to kill everything it started — gets `ERROR_ACCESS_DENIED`
  before Rhino ever starts, and `list_slots` shows nothing.

  Nothing on the agent side fixes this: the build path, the `version: "8"` string, and the call order
  are all irrelevant to it, so **do not re-verify the build or retry with different arguments**, and do
  not work around it by launching `Rhino.exe` yourself — an unmanaged Rhino is adopted, refuses
  `close_slot`, and holds the build lock (see §2). Say the live test is blocked by the host launcher
  policy, hand it to a session whose host permits breakaway, or ask the user to run it.
- **Modal-dialog commands wedge the slot.** `_PlugInManager`, `_Options` and friends open a dialog;
  `run_command` never returns, and after aborting the tool call the slot stays inside a command.
  Recovery is `close_slot` + `spawn_slot` — there is no in-place unwedge. Use script APIs, or the
  dash form of a command, instead.
- **Running a Rhino command from inside `run_csharp` wedges the slot too.** `RhinoApp.RunScript(...)`
  within a script call — even the dash form, even one that opens no dialog — blocked for the full 120 s
  and left the slot unusable. The two mechanisms do not nest: `run_command` for commands, `run_csharp`
  for RhinoCommon, never one inside the other.
- **Anything that replaces the document detaches the slot, and the detached Rhino cannot be closed.**
  `run_command(slot, "_-Open …")` reports the file read successfully, and then the *next* call fails with
  `rhino_closed` — "its document or the Rhino window was closed" — and the slot is pruned. The process
  does not exit. It is later re-adopted (§2), so `close_slot` refuses it, and it holds the build lock
  until stopped by PID. `_-New` is the same shape.

  So **do not reopen a document inside a live test.** To check that state survives a save and reload,
  either serialize and deserialize in-process (`TerrainSerializer.Serialize` → `Deserialize`, the same
  code path the document read uses), or spawn a *fresh* slot whose first action is the open — accepting
  that the slot is then single-use.
- **`Unknown command: _-ScriptEditor` from `run_python`/`run_csharp` is the wedge symptom**, not a
  missing script editor. Respawn the slot and the same script runs.
- **The C# script host has a minimal `using` set** — no `System.Linq`, so use `foreach` or fully
  qualify. Use the injected `__rhino_doc__`, never `RhinoDoc.ActiveDoc` or `scriptcontext.doc`.
- **A `Success` return is not evidence a plugin loaded.** See step 3.

## 8. Isolate a crash before changing code

When Rhino exits, narrow the boundary first. Add temporary, timestamped trace lines around
`GetObject.Get`, form construction, `form.Show()`, positioning, conduit enable and timer start; remove
them immediately after diagnosis. Reproduce in normal mode, then compare against `/safemode` (launch
that one manually — the router spawns normal mode).

- A managed exception caught at a trace boundary is an application/UI lifecycle bug: capture its type
  and message and fix that boundary.
- A process exit with no managed trace is a native host/plugin failure: record the slot PID and exit
  time and inspect `%TEMP%`, `%LOCALAPPDATA%\CrashDumps`, `C:\ProgramData\Microsoft\Windows\WER\` and
  the Application event log around it. Do not assume the WER helper process is the crashing component.

Keep the reproduction minimal: one curve, one command, one transition.

## 9. Eto/Rhino lifecycle checklist

- Show modeless document-parented forms through `Application.Instance.AsyncInvoke` after the command
  returns; set `Owner = RhinoEtoApp.MainWindowForDocument(doc)` and apply `UseRhinoStyle`.
- Start timers and enable display conduits from the form's `Shown` event, not during construction.
- Treat `SystemFonts.Default` and other shared Eto resources as borrowed; never dispose them from a
  paint pass. Dispose only resources the control owns.
- Guard refresh/paint paths while closing; stop the timer, disable/dispose the conduit, and dispose
  the edit session exactly once.
- Keep edits provisional until Apply, and commit the document mutation in one Rhino undo record.

## 10. Cleanup and evidence

`close_slot` the exact slot, delete temporary traces, rebuild if code changed, and run the relevant
tests (normally the full solution). Finish with `git status --short` and `git diff --check`.

**Then check no Rhino is left running.** `close_slot` reporting `closed: true` only accounts for the slot
named; `list_slots` plus the `Get-CimInstance` query in §2 accounts for the processes. A leaked
router-spawned Rhino is invisible until the next build fails on a file lock, by which point the cause is
several steps behind.

The report should include: Rhino version and slot id/PID, plugin build path and the load-verification
result, the exact command/script sequence, what was queried to confirm each assertion, which claims
rest on a screen capture and which on document state, any wedge/respawn, the cleanup result, and the
test commands and counts.
