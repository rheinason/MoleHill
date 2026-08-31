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

A copy/lock warning (`MSB3021`/`MSB3027`) is secondary to compiler errors, but a live test must run
the newly built assembly — close the locking process and rebuild rather than testing a stale binary.
Plugin path: `src\MoleHill.Rhino\bin\Debug\net7.0\MoleHill.Rhino.rhp`.

## 2. Spawn a disposable slot

```
spawn_slot(version: "8")   →   { slotId, port, pid, adopted }
```

Record `slotId` and `pid`; pass `slot` explicitly on every later call. `close_slot` at the end kills
exactly that instance. It refuses to close an *adopted* slot (a Rhino the user started), which is why
this path is safe — never `Stop-Process Rhino`, which would kill the user's unrelated session.

Confirm the slot is alive and the script host works before doing anything else:

```
run_csharp(slot, "Console.WriteLine(__rhino_doc__.Objects.Count);")
```

## 3. Load the plugin and confirm it really loaded

Load by path from a script — **not** via `_PlugInManager`, which opens a modal dialog and wedges the
slot (see Known failure modes):

```csharp
System.Guid id;
var r = Rhino.PlugIns.PlugIn.LoadPlugIn(
    @"C:\Users\hbxma\Dropbox\TopoTest\src\MoleHill.Rhino\bin\Debug\net7.0\MoleHill.Rhino.rhp",
    out id);
Console.WriteLine("result=" + r);          // LoadPlugInResult, not bool
```

Then verify two things, because `LoadPlugIn` reports `Success` for an already-registered plugin
whether or not this session actually loaded it:

- `get_commands(filter: "mh")` lists the MoleHill commands. Note that this also lists commands from
  *registered but unloaded* plugins, so it is necessary, not sufficient.
- Read the command history for a load refusal:

  ```csharp
  Console.WriteLine(Rhino.RhinoApp.CommandHistoryWindowText);
  ```

  A `Blocking plug-in MoleHill.Rhino.` line means Rhino refused the assembly, and anything observed
  afterwards is not a valid test of the build. Check the outputs for Mark of the Web
  (`Get-Item <file> -Stream Zone.Identifier`), unblock, then respawn the slot.

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

`get_viewport_image` captures the **viewport only** — correct for conduit output, meshes, curves and
display colour, and its metadata block (camera, framed bounds, on-screen object count) diagnoses an
empty capture without re-shooting.

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

- **Modal-dialog commands wedge the slot.** `_PlugInManager`, `_Options` and friends open a dialog;
  `run_command` never returns, and after aborting the tool call the slot stays inside a command.
  Recovery is `close_slot` + `spawn_slot` — there is no in-place unwedge. Use script APIs, or the
  dash form of a command, instead.
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

The report should include: Rhino version and slot id/PID, plugin build path and the load-verification
result, the exact command/script sequence, what was queried to confirm each assertion, which claims
rest on a screen capture and which on document state, any wedge/respawn, the cleanup result, and the
test commands and counts.
