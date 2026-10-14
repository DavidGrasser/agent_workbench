#!/usr/bin/env bash
# Packs the complete coding setup of this machine into one setup program,
# Agent-Workbench-Setup.exe, that restores it on another PC.
#
# Usage: export.sh [--folder] [--generic] [output-dir]
#   default output: the Windows Desktop
#   --folder    also keep the unpacked package next to the exe (install.bat route)
#   --generic   a setup others can use: no personal vault, MCP servers, Git identity,
#               CLAUDE.md or settings.json (--no-vault is the old name)
#
# Credentials never enter the package: MCP tokens are replaced by a placeholder
# and asked for again by the installer. A final scan aborts if a token leaked.

set -euo pipefail

KIT_DIR="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")" && pwd)"
VAULT="$(cd "$KIT_DIR/../.." && pwd)"
WIN_PROFILE="$(cmd.exe /c 'echo %USERPROFILE%' 2>/dev/null | tr -d '\r')"
WINHOME="$(wslpath -u "$WIN_PROFILE")"
KEEP_FOLDER=""
PERSONAL=1
while [ $# -gt 0 ]; do
    case "$1" in
        --folder) KEEP_FOLDER=1; shift ;;
        --generic|--no-vault) PERSONAL=""; shift ;;
        *) break ;;
    esac
done
OUT="${1:-$WINHOME/Desktop}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
TARGET="$WORK/agent-workbench-$(date +%F)"

say()  { printf '\033[34m[export]\033[0m %s\n' "$*"; }
warn() { printf '\033[33m[warn]\033[0m   %s\n' "$*"; }
die()  { printf '\033[31m[fail]\033[0m   %s\n' "$*"; exit 1; }

# The generic setup only needs the scripts beside the kit, not a vault.
[ -d "$VAULT/memory" ] || { [ -z "$PERSONAL" ] && [ -d "$VAULT/scripts" ]; } || die "Vault not found at $VAULT"
mkdir -p "$TARGET"/{home/.config,claude,mcp,windows/fonts}

say "Installer"
cp "$KIT_DIR"/install.sh "$KIT_DIR"/install.ps1 "$KIT_DIR"/install.bat "$KIT_DIR"/README.md "$TARGET/"
chmod +x "$TARGET/install.sh"

# A generic package carries the templates of templates/home and templates/windows, written
# for a neutral user; the installer rewrites these paths like any other old path.
if [ -n "$PERSONAL" ]; then
    FROM="$(hostname)"; OLD_HOME="$HOME"; OLD_WINHOME="$WINHOME"; OLD_WIN="$WIN_PROFILE"; OLD_VAULT="$VAULT"
else
    FROM="generic"; OLD_HOME="/home/agent-workbench-user"; OLD_WINHOME="/mnt/c/Users/agent-workbench-user"
    OLD_WIN='C:\Users\agent-workbench-user'; OLD_VAULT="$OLD_WINHOME/Documents/claude-vault"
fi

say "Manifest"
{
    echo "EXPORTED_AT='$(date '+%F %T')'"
    echo "EXPORTED_FROM='$FROM'"
    echo "OLD_HOME='$OLD_HOME'"
    echo "OLD_WINHOME='$OLD_WINHOME'"
    echo "OLD_WINHOME_WIN='$OLD_WIN'"
    echo "OLD_VAULT='$OLD_VAULT'"
    echo "OLD_DISTRO='${WSL_DISTRO_NAME:-Ubuntu}'"
    echo "GIT_NAME='$([ -n "$PERSONAL" ] && git config --global user.name || true)'"
    echo "GIT_EMAIL='$([ -n "$PERSONAL" ] && git config --global user.email || true)'"
    echo "CLAUDE_VERSION='$(claude --version 2>/dev/null | head -1 || true)'"
} > "$TARGET/manifest.env"

say "Home files (CLAUDE.md, tmux, starship, launchers)"
for f in .tmux.conf .tmux-fix-layout.sh .tmux-git-status.sh claude-tmux.sh claude-tmux-worktree.sh wt-cleanup.sh; do
    if [ -n "$PERSONAL" ] && [ -f "$HOME/$f" ]; then cp "$HOME/$f" "$TARGET/home/"
    elif [ -f "$KIT_DIR/templates/home/$f" ]; then cp "$KIT_DIR/templates/home/$f" "$TARGET/home/"
    else warn "missing ~/$f"; fi
done
# Before 13.10.2026 the workflow lived in ~/CLAUDE.md; now it is workflow/CLAUDE.md in the vault.
if [ -n "$PERSONAL" ] && [ -f "$HOME/CLAUDE.md" ] && [ ! -L "$HOME/CLAUDE.md" ]; then cp "$HOME/CLAUDE.md" "$TARGET/home/"; fi
if [ -n "$PERSONAL" ] && [ -f "$HOME/.config/starship.toml" ]; then cp "$HOME/.config/starship.toml" "$TARGET/home/.config/"
else cp "$KIT_DIR/templates/home/.config/starship.toml" "$TARGET/home/.config/"; fi
if [ -n "$PERSONAL" ]; then crontab -l 2>/dev/null | grep -v '^#' | grep . > "$TARGET/home/crontab.txt" || true
else cp "$KIT_DIR/templates/home/crontab.txt" "$TARGET/home/"; fi
cp "$KIT_DIR/templates/tool-tmux.sh" "$TARGET/home/tool-tmux.sh"

say "Claude settings and skills"
cp "$KIT_DIR/templates/settings.generic.json" "$TARGET/claude/"
if [ -n "$PERSONAL" ]; then
    [ -f "$HOME/.claude/settings.json" ] && cp "$HOME/.claude/settings.json" "$TARGET/claude/"
    [ -f "$HOME/.claude/keybindings.json" ] && cp "$HOME/.claude/keybindings.json" "$TARGET/claude/"
fi
mkdir -p "$TARGET/claude/skills"
for s in "$HOME"/.claude/skills/*/; do
    # synced/ holds the claude.ai account skills, they come back on login
    [ "$(basename "$s")" = "synced" ] && continue
    [ -n "$PERSONAL" ] && cp -r "$s" "$TARGET/claude/skills/"
done

say "MCP servers (without credentials)"
python3 - "$HOME/.claude.json" "$TARGET/mcp/servers.json" "$PERSONAL" <<'EOF'
import json, os, re, sys
src, dst, personal = sys.argv[1], sys.argv[2], sys.argv[3]
servers = json.load(open(src)).get("mcpServers", {}) if personal and os.path.exists(src) else {}
SECRET_KEY = re.compile(r"auth|token|key|secret|pass", re.I)

def strip(d):
    for k, v in d.items():
        if isinstance(v, str) and SECRET_KEY.search(k):
            scheme = v.split(" ", 1)[0] + " " if " " in v else ""
            d[k] = scheme + "{{SECRET}}"

for srv in servers.values():
    for section in ("headers", "env"):
        if isinstance(srv.get(section), dict):
            strip(srv[section])
json.dump(servers, open(dst, "w"), indent=2)
print("  " + ", ".join(servers) if servers else "  none")
EOF

say "Windows parts (terminal profile, context menu)"
APPDIR="$WINHOME/AppData/Roaming/ClaudeCode"
for f in ClaudeHere.ps1 claude-code.ico; do
    if [ -n "$PERSONAL" ] && [ -f "$APPDIR/$f" ]; then cp "$APPDIR/$f" "$TARGET/windows/"
    else cp "$KIT_DIR/templates/windows/$f" "$TARGET/windows/"; fi
done
for dir in "$WINHOME/AppData/Local/Microsoft/Windows/Fonts" /mnt/c/Windows/Fonts; do
    for style in Regular Bold Italic BoldItalic; do
        f="$dir/MesloLGSDZNerdFont-$style.ttf"
        [ -f "$f" ] && [ ! -f "$TARGET/windows/fonts/$(basename "$f")" ] && cp "$f" "$TARGET/windows/fonts/"
    done
done
[ -n "$(ls "$TARGET/windows/fonts")" ] || warn "MesloLGSDZ Nerd Font not found, setup will skip the font"
WT_SETTINGS="$(ls "$WINHOME"/AppData/Local/Packages/Microsoft.WindowsTerminal*/LocalState/settings.json 2>/dev/null | head -1 || true)"
if [ -z "$PERSONAL" ]; then
    cp "$KIT_DIR/templates/windows/terminal-fragment.json" "$TARGET/windows/"
elif [ -n "$WT_SETTINGS" ]; then
    python3 - "$WT_SETTINGS" "$TARGET/windows/terminal-fragment.json" <<'EOF'
import json, re, sys
text = open(sys.argv[1], encoding="utf-8-sig").read()
text = re.sub(r"^\s*//.*$", "", text, flags=re.M)
data = json.loads(text)
profiles = [p for p in data["profiles"]["list"] if p.get("name") == "Claude Code"]
schemes = [s for s in data.get("schemes", [])
           if profiles and s.get("name") == profiles[0].get("colorScheme")]
json.dump({"profiles": profiles, "schemes": schemes}, open(sys.argv[2], "w"), indent=2)
print(f"  profile: {bool(profiles)}, scheme: {bool(schemes)}")
EOF
else
    warn "Windows Terminal settings not found, the generic profile is used"
    cp "$KIT_DIR/templates/windows/terminal-fragment.json" "$TARGET/windows/"
fi

if [ -n "$PERSONAL" ]; then
    say "Obsidian vault ($(du -sh "$VAULT" | cut -f1))"
    rsync -a "$VAULT/" "$TARGET/vault/" \
        --exclude '.trash/' \
        --exclude 'scripts/hooks/*.log' \
        --exclude '.obsidian/plugins/graph-pulse/activity.jsonl' \
        --exclude '.obsidian/plugins/graph-pulse/diag.json'
else
    say "Obsidian vault left out (--generic)"
fi

say "Vault template (layout, Obsidian plugins, generic scripts)"
VT="$TARGET/vault-template"
cp -r "$KIT_DIR/templates/vault" "$VT"
find "$VT" -name '*.tmpl' -exec sh -c 'mv "$1" "${1%.tmpl}"' _ {} \;
rsync -a "$VAULT/.obsidian/" "$VT/.obsidian/" \
    --exclude 'workspace*.json' --exclude 'cache' \
    --exclude 'plugins/graph-pulse/activity.jsonl' --exclude 'plugins/graph-pulse/diag.json'
mkdir -p "$VT/scripts/hooks" "$VT/scripts/bin" "$VT/scripts/opencode"
cp -r "$KIT_DIR/templates/workflow" "$TARGET/workflow"
find "$TARGET/workflow" -name '*.tmpl' -exec sh -c 'mv "$1" "${1%.tmpl}"' _ {} \;
for f in scripts/vault-link.sh scripts/tool-sync.py scripts/secrets.py scripts/tool-hooks.py scripts/opencode/agent-workbench.js \
         scripts/hooks/hooks.json scripts/hooks/codex_hook.py scripts/hooks/session_context.py scripts/hooks/git_write_guard.py scripts/hooks/graph_pulse.py \
         scripts/bin/insights-monthly.sh .gitignore .gitattributes; do
    [ -f "$VAULT/$f" ] && cp "$VAULT/$f" "$VT/$f"
done
# Only the hooks whose script the template carries.
python3 - "$VT/scripts/hooks" <<'EOF'
import json, os, sys
folder = sys.argv[1]
path = os.path.join(folder, "hooks.json")
if os.path.exists(path):
    spec = json.load(open(path))
    for event, entries in spec.items():
        if not event.startswith("_"):
            spec[event] = [e for e in entries if os.path.isfile(os.path.join(folder, e["script"]))]
    json.dump(spec, open(path, "w"), indent=2)
EOF

say "Checking that no credential left the machine"
python3 - "$HOME/.claude.json" "$TARGET" <<'EOF'
import json, os, sys
servers = json.load(open(sys.argv[1])).get("mcpServers", {}) if os.path.exists(sys.argv[1]) else {}
secrets = set()
for srv in servers.values():
    for section in ("headers", "env"):
        for v in (srv.get(section) or {}).values():
            if isinstance(v, str):
                secrets.add(v.split(" ", 1)[-1])
secrets = {s for s in secrets if len(s) >= 12}
hits = []
for root, dirs, files in os.walk(sys.argv[2]):
    for f in files:
        p = os.path.join(root, f)
        try:
            data = open(p, "rb").read()
        except OSError:
            continue
        if any(s.encode() in data for s in secrets):
            hits.append(p)
if hits:
    print("Credential found in:\n  " + "\n  ".join(hits))
    sys.exit(1)
print(f"  {len(secrets)} token(s) checked, none found")
EOF

say "Setup program"
DOTNET="$(command -v dotnet || echo "$HOME/.dotnet/dotnet")"
if [ ! -x "$DOTNET" ]; then
    say ".NET SDK missing, installing it to ~/.dotnet (once)"
    curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir "$HOME/.dotnet" >/dev/null \
        || die ".NET SDK install failed"
    DOTNET="$HOME/.dotnet/dotnet"
fi
BUILD="$WORK/build"
cp -r "$KIT_DIR/setup-app" "$BUILD"
python3 -c 'import shutil,sys; shutil.make_archive(sys.argv[1], "zip", sys.argv[2])' "$BUILD/payload" "$TARGET"
DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 "$DOTNET" build "$BUILD" -c Release -o "$BUILD/out" -v quiet -nologo \
    | grep -E "error|warn" || true
[ -f "$BUILD/out/Agent-Workbench-Setup.exe" ] || die "build failed"

EXE="$OUT/Agent-Workbench-Setup-$(date +%F).exe"
cp "$BUILD/out/Agent-Workbench-Setup.exe" "$EXE"
if [ -n "$KEEP_FOLDER" ]; then
    rm -rf "$OUT/$(basename "$TARGET")"
    cp -r "$TARGET" "$OUT/"
    say "Folder: $OUT/$(basename "$TARGET")"
fi
say "Done: $EXE ($(du -sh "$EXE" | cut -f1))"
