#!/bin/bash
# Erstellt eine isolierte Worktree-Umgebung fuer alle Git-Repos
# in einem Verzeichnis und startet Claude Code darin.
# Das Original-Repo bleibt unberuehrt.

WORKTREE_BASE="/tmp/claude-worktrees"
MAX_AGE_DAYS=7

# --- Pfad-Konvertierung (identisch zu claude-tmux.sh) ---
RAW_PATH="${1:-}"
START_DIR=""

if [ -n "$RAW_PATH" ]; then
    if echo "$RAW_PATH" | grep -qi 'wsl.localhost'; then
        START_DIR=$(echo "$RAW_PATH" | sed 's|\\|/|g' | sed -E 's|^//wsl\.localhost/[^/]+||i')
    elif echo "$RAW_PATH" | grep -qE '^[A-Za-z]:'; then
        START_DIR=$(wslpath -u "$RAW_PATH" 2>/dev/null || echo "")
    elif [ -d "$RAW_PATH" ]; then
        START_DIR="$RAW_PATH"
    fi
fi

if [ -z "$START_DIR" ] || [ ! -d "$START_DIR" ]; then
    echo "Fehler: Kein gueltiges Verzeichnis angegeben: $RAW_PATH"
    echo "Usage: $0 <pfad-zu-src-verzeichnis>"
    read -p "Druecke Enter zum Schliessen..."
    exit 1
fi

# --- Alte Worktrees aufraeumen (aelter als MAX_AGE_DAYS) ---
cleanup_old() {
    [ ! -d "$WORKTREE_BASE" ] && return
    find "$WORKTREE_BASE" -maxdepth 1 -mindepth 1 -type d -mtime "+$MAX_AGE_DAYS" | while read old_env; do
        echo "Raeume alte Umgebung auf: $(basename "$old_env")"
        # Git worktree remove fuer jedes Repo (Env-Dir selbst + Unterordner)
        for wt_dir in "$old_env" "$old_env"/*/; do
            [ ! -d "$wt_dir" ] && continue
            if [ -d "$wt_dir/.git" ] || [ -f "$wt_dir/.git" ]; then
                git_common=$(cd "$wt_dir" && git rev-parse --git-common-dir 2>/dev/null)
                if [ -n "$git_common" ]; then
                    main_repo=$(cd "$wt_dir" && cd "$git_common/.." 2>/dev/null && pwd)
                    if [ -n "$main_repo" ]; then
                        (cd "$main_repo" && git worktree remove "$wt_dir" --force 2>/dev/null)
                        (cd "$main_repo" && git worktree prune 2>/dev/null)
                    fi
                fi
            fi
        done
        rm -rf "$old_env"
    done
}

cleanup_old

# Personal agents are symlinks in .claude/agents, kept out of git; a worktree only carries
# what git tracks, so the links are recreated in the new environment.
copy_agent_links() {
    for f in "$1"/.claude/agents/*.md; do
        [ -L "$f" ] || continue
        mkdir -p "$2/.claude/agents"
        ln -sfn "$(readlink -f "$f")" "$2/.claude/agents/$(basename "$f")"
    done
}

# --- Umgebungsname generieren ---
ENV_NAME="env-$(date +%Y%m%d-%H%M%S)"
ENV_DIR="$WORKTREE_BASE/$ENV_NAME"

echo "============================================"
echo "  Worktree-Umgebung wird erstellt..."
echo "  Name:   $ENV_NAME"
echo "  Quelle: $START_DIR"
echo "============================================"

# --- Erkennen ob START_DIR selbst ein Git-Repo ist ---
CLAUDE_DIR=""
ERRORS=0

if [ -d "$START_DIR/.git" ] || [ -f "$START_DIR/.git" ]; then
    # Einzelnes Repo: Worktree direkt als Env-Dir erstellen
    current_commit=$(cd "$START_DIR" && git rev-parse HEAD 2>/dev/null)
    current_branch=$(cd "$START_DIR" && git branch --show-current 2>/dev/null)
    display_ref="${current_branch:-$current_commit}"

    echo -n "  $(basename "$START_DIR") ($display_ref) ... "
    (cd "$START_DIR" && git worktree add --detach "$ENV_DIR" "$current_commit" 2>&1)
    if [ $? -eq 0 ]; then
        echo "OK"
    else
        echo "FEHLER"
        read -p "Druecke Enter zum Schliessen..."
        exit 1
    fi
    CLAUDE_DIR="$ENV_DIR"
else
    # Mehrere Repos: Env-Dir erstellen und jeden Unterordner verarbeiten
    mkdir -p "$ENV_DIR"

    for item in "$START_DIR"/*/; do
        [ ! -d "$item" ] && continue
        name=$(basename "$item")

        if [ -d "$item/.git" ] || [ -f "$item/.git" ]; then
            current_commit=$(cd "$item" && git rev-parse HEAD 2>/dev/null)
            current_branch=$(cd "$item" && git branch --show-current 2>/dev/null)
            display_ref="${current_branch:-$current_commit}"

            echo -n "  $name ($display_ref) ... "
            (cd "$item" && git worktree add --detach "$ENV_DIR/$name" "$current_commit" 2>&1)
            if [ $? -eq 0 ]; then
                echo "OK"
            else
                echo "FEHLER"
                ERRORS=$((ERRORS + 1))
            fi
        else
            echo "  $name (symlink)"
            ln -s "$item" "$ENV_DIR/$name"
        fi
    done

    # Einzelne Dateien verlinken (z.B. repos.yaml, addons.yaml)
    for file in "$START_DIR"/*; do
        [ -f "$file" ] || continue
        ln -s "$file" "$ENV_DIR/$(basename "$file")" 2>/dev/null
    done

    if [ $ERRORS -gt 0 ]; then
        echo ""
        echo "WARNUNG: $ERRORS Worktree(s) konnten nicht erstellt werden."
        read -p "Trotzdem fortfahren? (j/n) " choice
        [ "$choice" != "j" ] && exit 1
    fi
    CLAUDE_DIR="$ENV_DIR"
fi

# Links of the repository that contains the start folder, and of every nested repository.
copy_agent_links "$(git -C "$START_DIR" rev-parse --show-toplevel 2>/dev/null || echo "$START_DIR")" "$ENV_DIR"
for item in "$START_DIR"/*/; do
    [ -d "$ENV_DIR/$(basename "$item")" ] && [ ! -L "$ENV_DIR/$(basename "$item")" ] && copy_agent_links "$item" "$ENV_DIR/$(basename "$item")"
done

echo ""
echo "============================================"
echo "  Umgebung bereit: $CLAUDE_DIR"
echo "  Starte Claude Code..."
echo "============================================"

# --- Claude Code in eigener tmux-Session starten ---
SESSION="claude-wt-$(echo "$ENV_NAME" | sed 's/env-//')"

tmux new-session -d -s "$SESSION" -c "$CLAUDE_DIR" -n "Claude"
tmux set-environment -t "$SESSION" CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS 1
tmux send-keys -t "$SESSION:Claude" "cd '$CLAUDE_DIR' && CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1 claude --teammate-mode tmux --chrome" Enter
tmux new-window -t "$SESSION" -n "Console" -c "$CLAUDE_DIR"
tmux select-window -t "$SESSION:Claude"
tmux attach -t "$SESSION"
