#!/usr/bin/env bash
# Points every project's auto-memory directory at the central Obsidian vault,
# so recall is identical in every working directory.
# The vault is wherever this script lives (<vault>/scripts), also when called through
# the ~/.claude/vault-link.sh symlink, so a vault copied to another path keeps working.
VAULT_MEMORY="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")/.." && pwd)/memory"
PROJECTS="$HOME/.claude/projects"
[ -d "$VAULT_MEMORY" ] || exit 0
for d in "$PROJECTS"/*/; do
  [ -d "$d" ] || continue
  m="${d}memory"
  [ -L "$m" ] && continue
  # a real directory means unmerged local memories: keep them, do not link
  [ -d "$m" ] && continue
  ln -s "$VAULT_MEMORY" "$m"
done

# Global agents: everything in <vault>/agents, plus the sets of <vault>/project-agents
# that this machine lists in ~/.claude/global-agent-sets. The list stays outside the
# vault, so a set can be global on one PC and project-only on another.
VAULT="$(dirname "$VAULT_MEMORY")"
AGENTS="$HOME/.claude/agents"
SETS_FILE="$HOME/.claude/global-agent-sets"
if [ -L "$AGENTS" ]; then
  case "$(readlink -f "$AGENTS")" in "$VAULT"/*) rm "$AGENTS" ;; esac
fi
mkdir -p "$AGENTS"
wanted=()
for f in "$VAULT"/agents/*.md; do [ -f "$f" ] && wanted+=("$f"); done
if [ -f "$SETS_FILE" ]; then
  while IFS= read -r set; do
    set="${set%%#*}"; set="$(echo "$set" | tr -d '[:space:]')"
    [ -n "$set" ] || continue
    for f in "$VAULT/project-agents/$set"/*.md; do [ -f "$f" ] && wanted+=("$f"); done
  done < "$SETS_FILE"
fi
for f in "${wanted[@]}"; do
  ln -sfn "$f" "$AGENTS/$(basename "$f")"
done
# Drop links into the vault that are no longer wanted; anything else is the user's own.
for l in "$AGENTS"/*.md; do
  [ -L "$l" ] || continue
  target="$(readlink "$l")"
  case "$target" in "$VAULT"/*) ;; *) continue ;; esac
  keep=0
  for f in "${wanted[@]}"; do [ "$f" = "$target" ] && keep=1 && break; done
  [ $keep = 1 ] || rm "$l"
done
# Codex and OpenCode get the same agents, commands, skills and memory in their own formats.
if [ -d "$HOME/.config/opencode" ] || [ -d "$HOME/.codex" ]; then python3 "$VAULT/scripts/tool-sync.py"; fi
exit 0
