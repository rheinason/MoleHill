"""Interactive stdio client for the installed Rhino-MCP router.

Run from a shell that permits the router's managed Rhino process to break away
from its parent job. Send one JSON line per tool call: {"tool": "...", "args": {...}}.
The last returned slot can be referenced as "$lastSlot". Send {"tool": "quit"}
to exit. Any still-owned slot is closed in the cleanup block.
"""

from __future__ import annotations

import glob
import json
import os
import subprocess
import sys
from pathlib import Path


def find_router() -> Path:
    root = Path(os.environ["APPDATA"]) / "McNeel" / "Rhinoceros" / "packages"
    pattern = str(root / "8.0" / "Rhino-MCP-Platform" / "*" / "router" / "win-x64" / "rhino-mcp-router.exe")
    candidates = [Path(path) for path in glob.glob(pattern)]
    if not candidates:
        raise FileNotFoundError("Rhino-MCP router is not installed for Rhino 8")
    return max(candidates, key=lambda path: path.stat().st_mtime)


def find_value(value, key):
    if isinstance(value, str) and value.lstrip().startswith(("{", "[")):
        try:
            value = json.loads(value)
        except json.JSONDecodeError:
            return None
    if isinstance(value, dict):
        if value.get(key) is not None:
            return value[key]
        return next((found for child in value.values() if (found := find_value(child, key)) is not None), None)
    if isinstance(value, list):
        return next((found for child in value if (found := find_value(child, key)) is not None), None)
    return None


def use_slot(value, slot):
    if value == "$lastSlot":
        if slot is None:
            raise ValueError("$lastSlot used before spawn_slot returned a slot")
        return slot
    if isinstance(value, dict):
        return {key: use_slot(child, slot) for key, child in value.items()}
    if isinstance(value, list):
        return [use_slot(child, slot) for child in value]
    return value


def main():
    process = subprocess.Popen(
        [str(find_router()), "--default-version", "8"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=sys.stderr,
        text=True, encoding="utf-8", bufsize=1,
    )
    request_id = 0
    slot = None

    def request(method, params=None):
        nonlocal request_id
        request_id += 1
        process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": request_id,
                                        "method": method, "params": params or {}}, separators=(",", ":")) + "\n")
        process.stdin.flush()
        for line in process.stdout:
            message = json.loads(line)
            if message.get("id") == request_id:
                if "error" in message:
                    raise RuntimeError(message["error"])
                return message.get("result", {})
        raise RuntimeError(f"Rhino-MCP router exited ({process.poll()})")

    try:
        request("initialize", {"protocolVersion": "2025-03-26", "capabilities": {},
                               "clientInfo": {"name": "molehill-live-client", "version": "1.0"}})
        process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        process.stdin.flush()
        print("MCP_READY", flush=True)
        for line in sys.stdin:
            try:
                step = json.loads(line)
                if step["tool"] == "quit":
                    break
                args = use_slot(step.get("args", {}), slot)
                result = request("tools/call", {"name": step["tool"], "arguments": args})
                if returned := find_value(result, "slotId"):
                    slot = returned
                if step["tool"] == "close_slot" and find_value(result, "closed"):
                    slot = None
                print("MCP_RESULT=" + json.dumps(result, separators=(",", ":")), flush=True)
            except Exception as error:
                print("MCP_ERROR=" + repr(error), flush=True)
    finally:
        if slot is not None:
            try:
                result = request("tools/call", {"name": "close_slot", "arguments": {"slot": slot}})
                print("MCP_CLEANUP=" + json.dumps(result, separators=(",", ":")), flush=True)
            except Exception as error:
                print("MCP_CLEANUP_ERROR=" + repr(error), flush=True)
        process.stdin.close()
        try:
            process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            process.terminate()


if __name__ == "__main__":
    main()
