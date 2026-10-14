#!/usr/bin/env python3
"""Runs a Claude Code hook script for Codex.

Codex sends Claude's payload shape, with one difference: file changes arrive as
apply_patch, the whole patch in tool_input.command, possibly touching several
files. The script expects one Edit or Write per file, so it runs once per file
and the verdicts are merged. Everything else passes through unchanged.

Usage: codex_hook.py <script.py>, the script next to this file.
"""

import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.realpath(__file__))
PATCH_FILE = re.compile(r"^\*\*\* (Add|Update) File: (.+)$|^\*\*\* Move to: (.+)$", re.M)


def run(script, payload):
    proc = subprocess.run([sys.executable, script], input=json.dumps(payload),
                          capture_output=True, text=True)
    return proc.returncode, proc.stdout.strip(), proc.stderr.strip()


def patch_files(patch, cwd):
    files = []
    for match in PATCH_FILE.finditer(patch):
        kind = "Write" if match.group(1) == "Add" else "Edit"
        path = (match.group(2) or match.group(3)).strip()
        files.append((kind, path if os.path.isabs(path) else os.path.join(cwd, path)))
    return files


def main():
    script = os.path.join(HERE, sys.argv[1])
    raw = sys.stdin.read()
    try:
        payload = json.loads(raw)
    except ValueError:
        return 0
    if not os.path.isfile(script):
        return 0

    data = payload.get("tool_input") or {}
    command = data.get("command") if isinstance(data, dict) else None
    if isinstance(command, list):
        payload["tool_input"] = dict(data, command=" ".join(command))

    if payload.get("tool_name") != "apply_patch":
        code, out, err = run(script, payload)
        if out:
            print(out)
        if err:
            print(err, file=sys.stderr)
        return code

    reasons, denied = [], None
    for kind, path in patch_files(command if isinstance(command, str) else "", payload.get("cwd") or os.getcwd()):
        code, out, err = run(script, dict(payload, tool_name=kind, tool_input={"file_path": path}))
        if code == 2:
            print(err, file=sys.stderr)
            return 2
        try:
            verdict = json.loads(out) if out else {}
        except ValueError:
            continue
        if verdict.get("decision") == "block":
            reasons.append(verdict.get("reason", ""))
        elif (verdict.get("hookSpecificOutput") or {}).get("permissionDecision") == "deny":
            denied = denied or out
    if denied:
        print(denied)
    elif reasons:
        print(json.dumps({"decision": "block", "reason": "\n".join(reasons)}))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:
        sys.exit(0)
