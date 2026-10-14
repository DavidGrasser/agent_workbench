#!/usr/bin/env python3
"""SessionStart hook for Codex: brings the vault's agents, commands and skills up to date
(tool-sync.py, global and for this repository) and hands the memory index to the session
as context, the way Claude Code loads it through its auto-memory.

Never fails the session start: every error path ends quietly.
"""

import json
import os
import subprocess
import sys

VAULT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.realpath(__file__))))


def main():
    try:
        cwd = json.load(sys.stdin).get("cwd") or os.getcwd()
    except ValueError:
        cwd = os.getcwd()
    sync = os.path.join(VAULT, "scripts", "tool-sync.py")
    subprocess.run([sys.executable, sync], capture_output=True, timeout=20)
    root = subprocess.run(["git", "-C", cwd, "rev-parse", "--show-toplevel"], capture_output=True, text=True)
    if root.returncode == 0:
        subprocess.run([sys.executable, sync, "--project", root.stdout.strip()], capture_output=True, timeout=20)
    memory = os.path.join(VAULT, "memory", "MEMORY.md")
    if os.path.isfile(memory):
        print(f"Memory index of the knowledge vault ({memory}). Each line points at one fact file "
              f"in {os.path.dirname(memory)}; read the file before relying on it.\n")
        print(open(memory, encoding="utf-8").read())


if __name__ == "__main__":
    try:
        main()
    except Exception:
        pass
    sys.exit(0)
