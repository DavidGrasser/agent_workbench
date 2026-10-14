#!/usr/bin/env python3
"""Wires the vault's hooks (scripts/hooks/hooks.json) into Codex and OpenCode.

Codex: a marked block of [[hooks.<Event>]] tables at the end of ~/.codex/config.toml,
every script called through codex_hook.py. Codex runs a new or changed hook only
after it was trusted once with /hooks.
OpenCode: a link to the plugin scripts/opencode/agent-workbench.js, which reads
hooks.json itself.

Usage: tool-hooks.py codex|opencode
"""

import json
import os
import shlex
import shutil
import sys
import time
import tomllib

VAULT = os.path.dirname(os.path.dirname(os.path.realpath(__file__)))
HOOKS = os.path.join(VAULT, "scripts", "hooks")
HOME = os.path.expanduser("~")
BEGIN, END = "# >>> agent-workbench hooks >>>", "# <<< agent-workbench hooks <<<"


def codex():
    spec = json.load(open(os.path.join(HOOKS, "hooks.json"), encoding="utf-8"))
    block = [BEGIN]
    for event, entries in spec.items():
        if event.startswith("_"):
            continue
        for entry in entries:
            if not os.path.isfile(os.path.join(HOOKS, entry["script"])):
                continue
            matcher = entry["matcher"]
            # Codex reports its file changes as apply_patch.
            if any(name in matcher.split("|") for name in ("Edit", "Write")):
                matcher += "|apply_patch"
            command = "python3 %s %s" % (shlex.quote(os.path.join(HOOKS, "codex_hook.py")), entry["script"])
            block += ["", "[[hooks.%s]]" % event, "matcher = %s" % json.dumps("^(%s)$" % matcher),
                      "", "[[hooks.%s.hooks]]" % event, 'type = "command"', "command = %s" % json.dumps(command)]
            if entry.get("timeout"):
                block.append("timeout = %d" % entry["timeout"])
            if entry.get("async"):
                block.append("async = true")
    block.append(END)

    path = os.path.join(HOME, ".codex", "config.toml")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    text = open(path, encoding="utf-8").read() if os.path.exists(path) else ""
    if BEGIN in text and END in text:
        text = text[:text.index(BEGIN)] + text[text.index(END) + len(END):]
    new = text.rstrip("\n") + ("\n\n" if text.strip() else "") + "\n".join(block) + "\n"
    if new == (open(path, encoding="utf-8").read() if os.path.exists(path) else None):
        print("  [skip] codex hooks unchanged")
        return
    tomllib.loads(new)
    if os.path.exists(path):
        shutil.copy2(path, path + ".bak-" + time.strftime("%Y%m%d-%H%M%S"))
    open(path, "w", encoding="utf-8").write(new)
    os.chmod(path, 0o600)
    print("  [ok]   codex hooks written; trust them once with /hooks inside Codex")


def opencode():
    target = os.path.join(VAULT, "scripts", "opencode", "agent-workbench.js")
    link = os.path.join(HOME, ".config", "opencode", "plugins", "agent-workbench.js")
    os.makedirs(os.path.dirname(link), exist_ok=True)
    if os.path.islink(link) and os.readlink(link) == target:
        print("  [skip] opencode plugin already linked")
        return
    if os.path.lexists(link):
        os.rename(link, link + ".bak-" + time.strftime("%Y%m%d-%H%M%S"))
    os.symlink(target, link)
    print("  [ok]   opencode plugin linked")


if __name__ == "__main__":
    {"codex": codex, "opencode": opencode}[sys.argv[1]]()
