#!/bin/bash
# wt-cleanup: Loescht das aktuelle Worktree-Env, beendet Claude und schliesst die tmux-Session.

WORKTREE_BASE="/tmp/claude-worktrees"
CURRENT_DIR="$(pwd)"

# Pruefen ob wir in einem Worktree-Env sind
if [[ "$CURRENT_DIR" != "$WORKTREE_BASE/"* ]]; then
    echo "Fehler: Du bist nicht in einer Worktree-Umgebung."
    exit 1
fi

# Env-Name und tmux-Session ermitteln
ENV_NAME=$(echo "$CURRENT_DIR" | sed "s|$WORKTREE_BASE/||" | cut -d'/' -f1)
ENV_DIR="$WORKTREE_BASE/$ENV_NAME"
SESSION="claude-wt-$(echo "$ENV_NAME" | sed 's/env-//')"

echo "Loesche Umgebung: $ENV_NAME"

# 1) Alle Claude-Prozesse in dieser Session beenden
if tmux has-session -t "$SESSION" 2>/dev/null; then
    for pane_id in $(tmux list-panes -t "$SESSION" -a -F '#{pane_id}'); do
        pane_pid=$(tmux display-message -t "$pane_id" -p '#{pane_pid}')
        # Claude-Prozesse im Pane finden und beenden
        pgrep -P "$pane_pid" -f "claude" 2>/dev/null | xargs -r kill 2>/dev/null
    done
    sleep 1
fi

# 2) Git Worktrees sauber entfernen
#    Prueft das Env-Dir selbst UND alle Unterordner
MAIN_REPOS=()

remove_worktree() {
    local wt_dir="$1"
    if [ -d "$wt_dir/.git" ] || [ -f "$wt_dir/.git" ]; then
        local git_common=$(cd "$wt_dir" && git rev-parse --git-common-dir 2>/dev/null)
        if [ -n "$git_common" ]; then
            local main_repo=$(cd "$wt_dir" && cd "$git_common/.." 2>/dev/null && pwd)
            if [ -n "$main_repo" ]; then
                echo "  Worktree entfernen: $(basename "$wt_dir")"
                (cd "$main_repo" && git worktree remove "$wt_dir" --force 2>/dev/null)
                MAIN_REPOS+=("$main_repo")
            fi
        fi
    fi
}

# Env-Dir selbst (falls es direkt ein Worktree ist)
remove_worktree "$ENV_DIR"

# Unterordner (falls mehrere Repos)
for wt_dir in "$ENV_DIR"/*/; do
    [ ! -d "$wt_dir" ] && continue
    remove_worktree "$wt_dir"
done

# 3) Stale Worktree-Referenzen in den Original-Repos aufraeumen
for repo in "${MAIN_REPOS[@]}"; do
    (cd "$repo" && git worktree prune 2>/dev/null)
done

# 4) Env-Verzeichnis loeschen
rm -rf "$ENV_DIR"
echo "Umgebung geloescht."

# 5) tmux-Session beenden (macht auch dieses Terminal zu)
if tmux has-session -t "$SESSION" 2>/dev/null; then
    tmux kill-session -t "$SESSION"
fi
