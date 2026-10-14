#!/usr/bin/env python3
"""Keeps the secrets of MCP servers outside the vault, in ~/.config/agent-workbench/secrets.env.

Every shell loads that file (a marked line in ~/.bashrc and ~/.profile), so Claude Code
resolves ${NAME}, OpenCode {env:NAME} and Codex the environment of its servers alike.

Usage: secrets.py set NAME     (the value comes on stdin, never on the command line)
       secrets.py remove NAME
"""
import os
import re
import shlex
import sys

HOME = os.path.expanduser("~")
FILE = os.path.join(HOME, ".config", "agent-workbench", "secrets.env")
BEGIN, END = "# >>> agent-workbench secrets >>>", "# <<< agent-workbench secrets <<<"
LOADER = '[ -f "$HOME/.config/agent-workbench/secrets.env" ] && { set -a; . "$HOME/.config/agent-workbench/secrets.env"; set +a; }'


def read():
    values = {}
    if os.path.exists(FILE):
        for line in open(FILE, encoding="utf-8"):
            match = re.match(r"^([A-Za-z_][A-Za-z0-9_]*)=(.*)$", line.strip())
            if match:
                values[match.group(1)] = shlex.split(match.group(2))[0] if match.group(2) else ""
    return values


def save(values):
    os.makedirs(os.path.dirname(FILE), exist_ok=True)
    fd = os.open(FILE, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as fh:
        fh.write("# Secrets of MCP servers, written by Agent Workbench. Not part of the vault.\n")
        for name in sorted(values):
            fh.write(f"{name}={shlex.quote(values[name])}\n")
    os.chmod(FILE, 0o600)


def ensure_loaded():
    for rc in (".bashrc", ".profile"):
        path = os.path.join(HOME, rc)
        text = open(path, encoding="utf-8").read() if os.path.exists(path) else ""
        if BEGIN in text:
            continue
        with open(path, "a", encoding="utf-8") as fh:
            fh.write(("\n" if text and not text.endswith("\n") else "") + f"{BEGIN}\n{LOADER}\n{END}\n")


def main():
    if len(sys.argv) != 3 or sys.argv[1] not in ("set", "remove") or not re.match(r"^[A-Za-z_][A-Za-z0-9_]*$", sys.argv[2]):
        print(__doc__, file=sys.stderr)
        return 2
    action, name = sys.argv[1], sys.argv[2]
    values = read()
    if action == "set":
        values[name] = sys.stdin.read().strip()
    else:
        values.pop(name, None)
    save(values)
    ensure_loaded()
    return 0


if __name__ == "__main__":
    sys.exit(main())
