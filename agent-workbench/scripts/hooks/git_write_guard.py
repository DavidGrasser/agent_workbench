#!/usr/bin/env python3
"""PreToolUse guard: no writing git actions from a subagent or teammate.

Committing and pushing is the main agent's job, after the user approved it.
A subagent that commits on its own removes that approval step.

A subagent's PreToolUse payload carries `agent_type`; the main agent's does not.
That is the signal. Every decision is appended to hooks/git_write_guard.log, so
a call that could not be classified shows up in data rather than silently.
"""

import json
import os
import re
import sys

LOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "git_write_guard.log")
LOG_MAX_BYTES = 256 * 1024

WRITING = re.compile(
    r"\bgit\b[^|;&]*?\b(?:commit|push|add)\b"
    r"|\bgh\s+pr\s+(?:create|merge)\b"
)

# Payload / env keys that name the agent this call comes from.
AGENT_KEYS = ("agent_type", "subagent_type", "agent_name", "agent", "teammate_name")
AGENT_ENV = ("CLAUDE_AGENT_TYPE", "CLAUDE_SUBAGENT_TYPE", "CLAUDE_AGENT_NAME",
             "CLAUDE_TEAMMATE_NAME")
MAIN_AGENT_NAMES = {"", "main", "default", "claude"}


def note(payload, verdict):
    try:
        if os.path.exists(LOG) and os.path.getsize(LOG) > LOG_MAX_BYTES:
            return
        with open(LOG, "a", encoding="utf-8") as fh:
            fh.write(json.dumps({
                "verdict": verdict,
                "payload_keys": sorted(payload.keys()),
                "payload": payload,
                "claude_env": {k: v for k, v in os.environ.items()
                               if k.startswith("CLAUDE")},
            }) + "\n")
    except OSError:
        pass


def agent_identity(payload):
    """Name of the agent this call comes from, or None for the main agent.

    A subagent's PreToolUse payload carries `agent_type` and `agent_id`; the
    main agent's payload carries neither. Verified 2026-09-10 against a live
    subagent. `session_id` is useless here: a subagent shares the parent's.
    """
    for key in AGENT_KEYS:
        val = payload.get(key)
        if isinstance(val, str) and val.strip():
            return val.strip()
    for key in AGENT_ENV:
        val = os.environ.get(key, "")
        if val.strip():
            return val.strip()
    return None


def main():
    try:
        payload = json.load(sys.stdin)
    except Exception:
        return

    command = (payload.get("tool_input") or {}).get("command", "")
    if not command or not WRITING.search(command):
        return

    identity = agent_identity(payload)

    if identity is None:
        # Origin unknown: allow, but record the payload so this can be fixed.
        note(payload, "allowed-unclassified")
        return

    if identity.lower() in MAIN_AGENT_NAMES:
        note(payload, "allowed-main:%s" % identity)
        return

    note(payload, "denied:%s" % identity)

    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "deny",
            "permissionDecisionReason": (
                "Agent '%s' may not run writing git commands. Committing and pushing "
                "is the main agent's job, after the user approved it. Report the "
                "finished change instead and let the main agent commit."
            ) % identity,
        }
    }))


if __name__ == "__main__":
    main()
