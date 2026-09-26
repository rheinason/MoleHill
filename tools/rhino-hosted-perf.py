"""Hosted-performance lane driver: runs MoleHill's full-stack benchmarks inside a disposable Rhino.

Called by `validate.ps1 hosted-perf`; see docs/validation-lanes.md, "hosted-perf". It talks to the
installed Rhino-MCP router over stdio (the same router `rhino-live-client.py` uses), and:

  1. spawns a slot it owns - never adopts a Rhino the user started;
  2. loads the Release test assembly into its own AssemblyLoadContext, beside that build's own
     MoleHill.Core, so the plug-in's Debug Core already in the slot cannot be what gets measured;
  3. calls HostedPerformanceLane.Start, which runs on a background thread and returns at once -
     a run_csharp script executes on Rhino's UI thread, and holding it would stall what it measures;
  4. polls for the result file (written last, atomically), echoing progress lines;
  5. closes exactly the slot it spawned, whatever happened.

Exit code: 0 when the run produced a result file, 2 when it did not. Judging the result against the
baseline is the caller's job, from the JSON.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent


def load_live_client():
    spec = importlib.util.spec_from_file_location("rhino_live_client", HERE / "rhino-live-client.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


live = load_live_client()


class Router:
    def __init__(self):
        self.process = subprocess.Popen(
            [str(live.find_router()), "--default-version", "8"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=sys.stderr,
            text=True, encoding="utf-8", bufsize=1,
        )
        self.request_id = 0
        self.request("initialize", {"protocolVersion": "2025-03-26", "capabilities": {},
                                    "clientInfo": {"name": "molehill-hosted-perf", "version": "1.0"}})
        self.process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        self.process.stdin.flush()

    def request(self, method, params=None):
        self.request_id += 1
        self.process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": self.request_id, "method": method,
                                             "params": params or {}}, separators=(",", ":")) + "\n")
        self.process.stdin.flush()
        for line in self.process.stdout:
            message = json.loads(line)
            if message.get("id") == self.request_id:
                if "error" in message:
                    raise RuntimeError(message["error"])
                return message.get("result", {})
        raise RuntimeError(f"Rhino-MCP router exited ({self.process.poll()})")

    def call(self, tool, **arguments):
        result = self.request("tools/call", {"name": tool, "arguments": arguments})
        if result.get("isError"):
            raise RuntimeError(f"{tool} failed: {json.dumps(result)[:2000]}")
        return result

    def close(self):
        try:
            self.process.stdin.close()
            self.process.wait(timeout=3)
        except Exception:
            self.process.terminate()


BREAKAWAY_HINT = (
    "\n'Access is denied' from spawn_slot is the host, not the lane: this shell runs inside a Job Object "
    "that forbids the router's Rhino from breaking away (docs/rhino-live-testing.md section 7). Run the "
    "lane from an ordinary terminal, or run the --print-script output in a slot you drive yourself and "
    "judge its result with: ./validate.ps1 hosted-perf -HostedResult <result.json>"
)


def csharp_literal(path: Path) -> str:
    return '@"' + str(path).replace('"', '""') + '"'


def hosting_script(bin_dir: Path, request_path: Path) -> str:
    # The script host has a minimal using set: everything is fully qualified. RhinoCommon is never
    # preloaded here - it must resolve to the host's copy in the default context.
    return f"""
var dir = {csharp_literal(bin_dir)};
var alc = new System.Runtime.Loader.AssemblyLoadContext("molehill-hosted-perf-" + System.Guid.NewGuid().ToString("N"));
alc.Resolving += (ctx, name) => {{
    var p = System.IO.Path.Combine(dir, name.Name + ".dll");
    return System.IO.File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
}};
alc.LoadFromAssemblyPath(System.IO.Path.Combine(dir, "MoleHill.Core.dll"));
alc.LoadFromAssemblyPath(System.IO.Path.Combine(dir, "MoleHill.Interop.dll"));
var asm = alc.LoadFromAssemblyPath(System.IO.Path.Combine(dir, "MoleHill.Rhino.Tests.dll"));
asm.GetType("MoleHill.Rhino.Tests.HostedPerformanceLane").GetMethod("Start").Invoke(null, new object[] {{ {csharp_literal(request_path)} }});
Console.WriteLine("HOSTED_PERF_STARTED");
"""


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--bin", required=True, type=Path, help="Release output dir of MoleHill.Rhino.Tests")
    parser.add_argument("--request", required=True, type=Path, help="HostedPerformanceLane request JSON")
    parser.add_argument("--timeout-minutes", type=float, default=45.0)
    parser.add_argument("--print-script", action="store_true",
                        help="Print the C# hosting script and exit, for a host that drives its own slot "
                             "(for example an agent whose shell may not spawn Rhino - see BREAKAWAY_HINT).")
    args = parser.parse_args()

    if args.print_script:
        print(hosting_script(args.bin.resolve(), args.request.resolve()))
        return 0

    request = json.loads(args.request.read_text(encoding="utf-8-sig"))
    result_path = Path(request["ResultPath"])
    progress_path = Path(str(result_path) + ".progress")
    for stale in (result_path, progress_path):
        stale.unlink(missing_ok=True)

    router = Router()
    slot = None
    try:
        spawned = router.call("spawn_slot", version="8")
        slot = live.find_value(spawned, "slotId")
        if not slot:
            detail = json.dumps(spawned)[:1000]
            hint = BREAKAWAY_HINT if "Access is denied" in detail else ""
            raise RuntimeError(f"spawn_slot returned no slot: {detail}{hint}")
        if live.find_value(spawned, "adopted"):
            # Never measure in, or later close, a Rhino this run did not start.
            slot = None
            raise RuntimeError("spawn_slot returned an adopted Rhino; refusing to run the lane in it.")
        print(f"Spawned slot {slot} (pid {live.find_value(spawned, 'pid')})", flush=True)

        started = router.call("run_csharp", slot=slot, script=hosting_script(args.bin.resolve(), args.request.resolve()))
        if "HOSTED_PERF_STARTED" not in json.dumps(started):
            raise RuntimeError(f"The hosting script did not start the run: {json.dumps(started)[:2000]}")

        deadline = time.monotonic() + args.timeout_minutes * 60
        echoed = 0
        while not result_path.exists():
            if time.monotonic() > deadline:
                raise RuntimeError(f"No result after {args.timeout_minutes} minutes.")
            if progress_path.exists():
                lines = progress_path.read_text(encoding="utf-8", errors="replace").splitlines()
                for line in lines[echoed:]:
                    print("  " + line, flush=True)
                echoed = len(lines)
            time.sleep(2)

        if progress_path.exists():
            for line in progress_path.read_text(encoding="utf-8", errors="replace").splitlines()[echoed:]:
                print("  " + line, flush=True)
        print(f"Result: {result_path}", flush=True)
        return 0
    except Exception as error:
        print(f"HOSTED_PERF_ERROR: {error}", file=sys.stderr, flush=True)
        return 2
    finally:
        if slot is not None:
            try:
                closed = router.call("close_slot", slot=slot)
                print(f"Closed slot {slot}: {live.find_value(closed, 'closed')}", flush=True)
            except Exception as error:
                print(f"Could not close slot {slot}: {error}. Close it by PID after reading its command line "
                      "(docs/rhino-live-testing.md section 2).", file=sys.stderr, flush=True)
        router.close()


if __name__ == "__main__":
    sys.exit(main())
