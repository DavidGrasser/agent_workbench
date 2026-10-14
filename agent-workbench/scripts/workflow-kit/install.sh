#!/usr/bin/env bash
# Restores the exported Agent Workbench setup inside WSL. Driven by the setup app,
# but runs standalone as well. Safe to run again: existing files are backed up
# as *.bak-<timestamp> before being replaced, steps already done are skipped.
#
# Environment:
#   COMPONENTS        comma list of shell,claude,codex,opencode,vault,config,mcp,browser,cron,git
#                     (default: shell,claude,vault,config,mcp,cron,git)
#   VAULT_DIR         the vault folder (default <Windows home>/Documents/claude-vault)
#   VAULT_SOURCE      package: copy the vault inside this package (default when it has one)
#                     template: start a new vault from the template
#                     existing: use the folder as it is
#   ADD_WORKFLOW      1: copy the generic coding workflow into the vault (never overwrites)
#   CHROME            0: start Claude without --chrome (default 1)
#   MCP_SERVERS_FILE  JSON {"name": {claude mcpServers entry}}; when set, exactly these
#                     servers are written and nothing is prompted. Deleted after reading.

set -uo pipefail

BUNDLE="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")" && pwd)"
STAMP="$(date +%Y%m%d-%H%M%S)"
COMPONENTS="${COMPONENTS:-shell,claude,vault,config,mcp,cron,git}"

if [ -t 1 ]; then B=$'\033[34m' G=$'\033[32m' D=$'\033[2m' Y=$'\033[33m' R=$'\033[31m' N=$'\033[0m'
else B="" G="" D="" Y="" R="" N=""; fi
say()  { printf '\n%s==>%s %s\n' "$B" "$N" "$*"; }
ok()   { printf '  %s[ok]%s   %s\n' "$G" "$N" "$*"; }
skip() { printf '  %s[skip]%s %s\n' "$D" "$N" "$*"; }
warn() { printf '  %s[warn]%s %s\n' "$Y" "$N" "$*"; WARNINGS=$((WARNINGS + 1)); }
die()  { printf '  %s[fail]%s %s\n' "$R" "$N" "$*"; exit 1; }
want() { [[ ",$COMPONENTS," == *",$1,"* ]]; }
WARNINGS=0

[ -f "$BUNDLE/manifest.env" ] || die "manifest.env missing, this is not an export folder"
# shellcheck disable=SC1091
. "$BUNDLE/manifest.env"

WIN_PROFILE="$(cd /mnt/c 2>/dev/null; cmd.exe /c 'echo %USERPROFILE%' 2>/dev/null | tr -d '\r')"
[ -n "$WIN_PROFILE" ] || die "Windows home not reachable (cmd.exe), run this inside WSL"
WINHOME="$(wslpath -u "$WIN_PROFILE")"
VAULT="${VAULT_DIR:-$WINHOME/Documents/claude-vault}"
if [ -z "${VAULT_SOURCE:-}" ]; then
    if [ -d "$BUNDLE/vault" ]; then VAULT_SOURCE=package; else VAULT_SOURCE=template; fi
fi

echo "Package from $EXPORTED_FROM, exported $EXPORTED_AT"
echo "  Linux home:   $OLD_HOME -> $HOME"
echo "  Windows home: $OLD_WINHOME -> $WINHOME"
echo "  Vault:        $VAULT ($VAULT_SOURCE)"
echo "  Components:   $COMPONENTS"

# Rewrites the old machine's paths in every text file below the given paths.
rewrite_paths() {
    local old_win_fwd="${OLD_WINHOME_WIN//\\//}" new_win_fwd="${WIN_PROFILE//\\//}"
    local pairs=("$OLD_VAULT" "$VAULT" "$OLD_WINHOME" "$WINHOME" "$OLD_HOME" "$HOME"
                 "$OLD_WINHOME_WIN" "$WIN_PROFILE" "$old_win_fwd" "$new_win_fwd")
    local changed=0 i files
    for ((i = 0; i < ${#pairs[@]}; i += 2)); do
        [ "${pairs[i]}" = "${pairs[i+1]}" ] && continue
        files="$(grep -rlIF --exclude-dir=.git -- "${pairs[i]}" "$@" 2>/dev/null || true)"
        [ -z "$files" ] && continue
        while IFS= read -r f; do
            OLD="${pairs[i]}" NEW="${pairs[i+1]}" perl -pi -e 's/\Q$ENV{OLD}\E/$ENV{NEW}/g' "$f"
            changed=$((changed + 1))
        done <<< "$files"
    done
    [ -n "${QUIET:-}" ] && return
    [ $changed -gt 0 ] && ok "paths adjusted in $changed file(s)" || skip "paths already match"
}

# Copies src to dst with paths adjusted, keeping a backup of a differing file
# already there.
place() {
    local src="$1" dst="$2" tmp
    tmp="$(mktemp -d)/$(basename "$dst")"
    cp -r "$src" "$tmp"
    QUIET=1 rewrite_paths "$tmp"
    if [ "${CHROME:-1}" = 0 ]; then
        case "$(basename "$dst")" in claude-tmux*.sh) sed -i 's/ --chrome//g' "$tmp" ;; esac
    fi
    mkdir -p "$(dirname "$dst")"
    if [ -e "$dst" ] && ! [ -L "$dst" ] && diff -rq "$tmp" "$dst" >/dev/null 2>&1; then
        skip "$dst unchanged"; rm -rf "$(dirname "$tmp")"; return
    fi
    [ -e "$dst" ] || [ -L "$dst" ] && mv "$dst" "$dst.bak-$STAMP"
    mv "$tmp" "$dst" && rm -rf "$(dirname "$tmp")"
    ok "$dst"
}

# Points ~/.claude/<name> at a folder or file inside the vault.
link() {
    local target="$1" name="$2"
    if [ "$(readlink "$name" 2>/dev/null)" = "$target" ]; then
        skip "$name already linked"; return
    fi
    [ -e "$name" ] || [ -L "$name" ] && mv "$name" "$name.bak-$STAMP"
    ln -s "$target" "$name"
    ok "$name -> $target"
}

# ---------------------------------------------------------------------------
say "System packages"
missing=()
for p in tmux git curl python3 rsync perl; do command -v "$p" >/dev/null || missing+=("$p"); done
if [ ${#missing[@]} -gt 0 ]; then
    echo "  installing: ${missing[*]} (sudo password needed)"
    sudo apt-get update -qq && sudo apt-get install -y -qq "${missing[@]}" && ok "${missing[*]}" \
        || die "apt-get failed"
else
    skip "tmux git curl python3 rsync perl present"
fi

mkdir -p "$HOME/.local/bin"
export PATH="$HOME/.local/bin:$PATH"

if want claude; then
    say "Claude Code"
    if command -v claude >/dev/null; then
        skip "claude $(claude --version 2>/dev/null | head -1)"
    else
        curl -fsSL https://claude.ai/install.sh | bash && ok "installed" || warn "Claude Code install failed"
    fi
fi

# Codex and OpenCode share the tool-neutral core of the workflow: <vault>/workflow/AGENTS.md.
install_launcher() {
    local tool="$1" dst="$HOME/$1-tmux.sh"
    [ -f "$BUNDLE/home/tool-tmux.sh" ] || return 0
    local tmp; tmp="$(mktemp)"
    sed "s/@TOOL@/$tool/g" "$BUNDLE/home/tool-tmux.sh" > "$tmp"
    place "$tmp" "$dst"
    chmod +x "$dst"
    rm -f "$tmp"
}

if want codex; then
    say "Codex CLI"
    if [ -x "$HOME/.local/bin/codex" ] || command -v codex >/dev/null; then
        skip "codex present"
    else
        curl -fsSL https://chatgpt.com/codex/install.sh | CODEX_INSTALL_DIR="$HOME/.local/bin" CODEX_NON_INTERACTIVE=1 sh >/dev/null \
            && ok "installed" || warn "Codex install failed"
    fi
    mkdir -p "$HOME/.codex"
    install_launcher codex
fi

if want opencode; then
    say "OpenCode"
    if [ -x "$HOME/.opencode/bin/opencode" ] || command -v opencode >/dev/null; then
        skip "opencode present"
    else
        curl -fsSL https://opencode.ai/install | bash >/dev/null 2>&1 \
            && ok "installed" || warn "OpenCode install failed"
    fi
    mkdir -p "$HOME/.config/opencode"
    install_launcher opencode
fi

if want vault; then
    say "Obsidian vault"
    case "$VAULT_SOURCE" in
        existing)
            [ -d "$VAULT" ] && ok "using the existing vault" || warn "$VAULT does not exist"
            ;;
        package|template)
            src="$BUNDLE/vault"; [ "$VAULT_SOURCE" = template ] && src="$BUNDLE/vault-template"
            if [ ! -d "$src" ]; then
                warn "this package contains no $VAULT_SOURCE vault"
            elif [ -n "$(ls -A "$VAULT" 2>/dev/null)" ]; then
                skip "$VAULT is not empty, left untouched"
            else
                mkdir -p "$VAULT"
                rsync -a "$src/" "$VAULT/" && ok "$VAULT_SOURCE copied to $VAULT"
                rewrite_paths "$VAULT"
                [ "$VAULT_SOURCE" = template ] && NEW_VAULT=1
            fi
            ;;
    esac
fi

if [ -n "${ADD_WORKFLOW:-}" ]; then
    say "Coding workflow"
    if [ ! -d "$BUNDLE/workflow" ]; then
        warn "this package contains no workflow template"
    else
        mkdir -p "$VAULT/agents" "$VAULT/workflow"
        for f in "$BUNDLE"/workflow/agents/*.md "$BUNDLE/workflow/CLAUDE.md" "$BUNDLE/workflow/AGENTS.md"; do
            [ -f "$f" ] || continue
            case "$(basename "$f")" in
                CLAUDE.md|AGENTS.md) t="$VAULT/workflow/$(basename "$f")" ;;
                *) t="$VAULT/agents/$(basename "$f")" ;;
            esac
            if [ -e "$t" ]; then skip "${t#$VAULT/} exists, kept"; continue; fi
            # AGENTS.md is read by every tool; ~/.claude/vault only exists with the Claude configuration.
            if [ "$(basename "$f")" = AGENTS.md ]; then sed "s|~/.claude/vault|$VAULT|g" "$f" > "$t"; else cp "$f" "$t"; fi && ok "${t#$VAULT/}"
        done
    fi
fi

# The obsidian-git plugin of the template expects a repository; committed after the
# workflow step so the first commit holds the complete vault.
if [ -n "${NEW_VAULT:-}" ] && [ ! -d "$VAULT/.git" ]; then
    git -C "$VAULT" init -q && git -C "$VAULT" add -A \
        && git -C "$VAULT" -c user.name="Agent Workbench Setup" -c user.email=setup@localhost commit -q -m "New vault" \
        && ok "vault: git repository initialised" || warn "vault: git init failed"
fi

# Only now, after the vault and the workflow step, does a new vault have the shared core.
if want codex || want opencode; then
    say "Codex and OpenCode: instructions, agents, hooks"
    if [ -f "$VAULT/workflow/AGENTS.md" ]; then
        want codex && link "$VAULT/workflow/AGENTS.md" "$HOME/.codex/AGENTS.md"
        # Without its own AGENTS.md, OpenCode falls back to ~/.claude/CLAUDE.md with Claude's team mechanics.
        want opencode && link "$VAULT/workflow/AGENTS.md" "$HOME/.config/opencode/AGENTS.md"
    else
        warn "no workflow/AGENTS.md in the vault, the tools start without shared instructions"
    fi
    # Skills live in ~/.claude/skills, which OpenCode reads and tool-sync.py links for Codex;
    # without the Claude configuration step nobody else puts them there.
    if ! want config; then
        for sk in "$BUNDLE"/claude/skills/*/; do
            [ -d "$sk" ] && place "${sk%/}" "$HOME/.claude/skills/$(basename "$sk")"
        done
    fi
    # Agents, commands, skills and the memory index in each tool's own format.
    if [ -f "$VAULT/scripts/tool-sync.py" ]; then
        python3 "$VAULT/scripts/tool-sync.py" && ok "agents, commands and memory synced" \
            && { want opencode && ok "OpenCode: $(ls "$HOME/.config/opencode/agents" 2>/dev/null | wc -l) agents, $(ls "$HOME/.config/opencode/commands" 2>/dev/null | wc -l) commands"; \
                 want codex && ok "Codex: $(ls "$HOME/.codex/agents" 2>/dev/null | wc -l) agents, $(ls "$HOME/.agents/skills" 2>/dev/null | wc -l) skills"; true; }
    fi
    # The same hook scripts Claude Code runs, from scripts/hooks/hooks.json.
    if [ -f "$VAULT/scripts/tool-hooks.py" ]; then
        want codex && { python3 "$VAULT/scripts/tool-hooks.py" codex || warn "codex hooks not written"; }
        want opencode && { python3 "$VAULT/scripts/tool-hooks.py" opencode || warn "opencode plugin not linked"; }
    else
        skip "the vault has no scripts/tool-hooks.py, no hooks for Codex and OpenCode"
    fi
fi

if want browser; then
    say "Browser for Codex and OpenCode (Playwright MCP)"
    command -v npx >/dev/null || warn "npx missing, the setup installs Node.js as root"
    [ -x /opt/google/chrome/chrome ] || warn "Google Chrome missing, the setup installs it as root"
    python3 - "$HOME" "$( want codex && echo 1 )" "$( want opencode && echo 1 )" <<'EOF'
import json, os, sys, tomllib
home, codex, opencode = sys.argv[1], sys.argv[2] == "1", sys.argv[3] == "1"
args = ["-y", "@playwright/mcp@latest", "--browser", "chrome"]
if codex:
    path = os.path.join(home, ".codex", "config.toml")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    lines = open(path).read().splitlines() if os.path.exists(path) else []
    kept, skipping = [], False
    for line in lines:
        if line.lstrip().startswith("["):
            h = line.strip()
            skipping = h == "[mcp_servers.playwright]" or h.startswith("[mcp_servers.playwright.")
        if not skipping:
            kept.append(line)
    # Above the hooks block, which has to stay last.
    block = ["", "[mcp_servers.playwright]", 'command = "npx"', "args = " + json.dumps(args)]
    marker = "# >>> agent-workbench hooks >>>"
    at = kept.index(marker) if marker in kept else len(kept)
    kept[at:at] = block + ([""] if at < len(kept) else [])
    text = "\n".join(kept).strip("\n") + "\n"
    tomllib.loads(text)
    open(path, "w").write(text)
    os.chmod(path, 0o600)
    print("  [ok]   codex: playwright")
if opencode:
    path = os.path.join(home, ".config", "opencode", "opencode.json")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    try:
        data = json.load(open(path)) if os.path.exists(path) else {"$schema": "https://opencode.ai/config.json"}
    except ValueError:
        print("  [warn] opencode.json has comments (JSONC); add playwright in the Agent Workbench app")
        data = None
    if data is not None:
        data.setdefault("mcp", {})["playwright"] = {"type": "local", "command": ["npx"] + args, "enabled": True}
        json.dump(data, open(path, "w"), indent=2)
        os.chmod(path, 0o600)
        print("  [ok]   opencode: playwright")
EOF
fi

if want shell; then
    say "Starship prompt"
    if command -v starship >/dev/null; then
        skip "starship present"
    else
        curl -fsSL https://starship.rs/install.sh | sh -s -- -y -b "$HOME/.local/bin" >/dev/null \
            && ok "installed" || warn "starship install failed"
    fi

    say "Terminal files (tmux, starship, launchers)"
    for f in "$BUNDLE"/home/{.tmux.conf,.tmux-fix-layout.sh,.tmux-git-status.sh}; do
        [ -f "$f" ] && place "$f" "$HOME/$(basename "$f")"
    done
    if want claude; then
        for f in "$BUNDLE"/home/{claude-tmux.sh,claude-tmux-worktree.sh,wt-cleanup.sh}; do
            [ -f "$f" ] && place "$f" "$HOME/$(basename "$f")"
        done
    fi
    [ -f "$BUNDLE/home/.config/starship.toml" ] && place "$BUNDLE/home/.config/starship.toml" "$HOME/.config/starship.toml"
    chmod +x "$HOME"/.tmux-*.sh "$HOME"/claude-tmux*.sh "$HOME"/wt-cleanup.sh 2>/dev/null

    say "tmux plugins"
    while read -r spec; do
        repo="${spec%%#*}"; tag=""; [ "$repo" != "$spec" ] && tag="${spec#*#}"
        dir="$HOME/.tmux/plugins/${repo##*/}"
        if [ -d "$dir/.git" ]; then skip "${repo##*/}"; continue; fi
        git -c advice.detachedHead=false clone -q --depth 1 ${tag:+--branch "$tag"} "https://github.com/$repo" "$dir" \
            && ok "${repo##*/}" || warn "${repo##*/} clone failed"
    done < <(grep -E "^[[:space:]]*set -g @plugin" "$HOME/.tmux.conf" | sed -E "s/.*@plugin[[:space:]]+'([^']+)'.*/\1/")

    say "Shell (~/.bashrc)"
    BEGIN="# >>> claude-workflow >>>"; END="# <<< claude-workflow <<<"
    touch "$HOME/.bashrc"
    sed -i "/^$BEGIN\$/,/^$END\$/d" "$HOME/.bashrc"
    cat >> "$HOME/.bashrc" <<EOF
$BEGIN
case ":\$PATH:" in *":\$HOME/.local/bin:"*) ;; *) export PATH="\$PATH:\$HOME/.local/bin" ;; esac
[ -d "\$HOME/.opencode/bin" ] && case ":\$PATH:" in *":\$HOME/.opencode/bin:"*) ;; *) export PATH="\$PATH:\$HOME/.opencode/bin" ;; esac
$( [ "${CHROME:-1}" = 1 ] && want claude && echo "alias claude='claude --chrome'" )
$( want claude && echo 'alias wt-cleanup="$HOME/wt-cleanup.sh"' )
command -v starship >/dev/null && eval "\$(starship init bash)"
$END
EOF
    ok "$( [ "${CHROME:-1}" = 1 ] && want claude && echo "claude --chrome alias, " )$( want claude && echo "wt-cleanup, " )starship"
fi

if want config; then
    say "Claude configuration (~/.claude)"
    [ -d "$VAULT" ] || warn "vault missing at $VAULT, agents, commands and hooks will not resolve"
    mkdir -p "$HOME/.claude/skills"
    [ -d "$VAULT" ] && link "$VAULT" "$HOME/.claude/vault"
    for pair in commands:commands scripts/hooks:hooks scripts/bin:bin workflow/CLAUDE.md:CLAUDE.md \
                scripts/vault-link.sh:vault-link.sh scripts/vault-scan.py:vault-scan.py; do
        target="$VAULT/${pair%%:*}"
        if [ -e "$target" ]; then link "$target" "$HOME/.claude/${pair##*:}"; else skip "${pair%%:*} not in the vault"; fi
    done
    # vault-link.sh links the agents one by one (agents/ plus the sets this PC lists in
    # ~/.claude/global-agent-sets) and keeps them in sync at every Claude start.
    if [ -f "$VAULT/scripts/vault-link.sh" ]; then
        bash "$VAULT/scripts/vault-link.sh" && ok "agents linked from the vault ($(ls "$HOME/.claude/agents" | wc -l))"
    elif [ -e "$VAULT/agents" ]; then
        link "$VAULT/agents" "$HOME/.claude/agents"
    fi
    [ -f "$BUNDLE/claude/keybindings.json" ] && place "$BUNDLE/claude/keybindings.json" "$HOME/.claude/keybindings.json"
    for s in "$BUNDLE"/claude/skills/*/; do
        [ -d "$s" ] && place "${s%/}" "$HOME/.claude/skills/$(basename "$s")"
    done

    # The personal workflow and settings travel with the packaged vault only.
    settings="$BUNDLE/claude/settings.generic.json"
    if [ "$VAULT_SOURCE" = package ] && [ -f "$BUNDLE/claude/settings.json" ]; then
        [ -f "$BUNDLE/home/CLAUDE.md" ] && place "$BUNDLE/home/CLAUDE.md" "$HOME/CLAUDE.md"
        settings="$BUNDLE/claude/settings.json"
    fi
    # A hook whose script is not in this vault would fail on every tool call. Filtered before
    # placing, so an unchanged setup compares equal on the next run.
    filtered="$(mktemp -d)/settings.json"
    cp "$settings" "$filtered"
    QUIET=1 rewrite_paths "$filtered"
    python3 - "$filtered" "$HOME" <<'EOF'
import json, os, re, sys
path, home = sys.argv[1], sys.argv[2]
settings = json.load(open(path))
dropped = []
for event, groups in list(settings.get("hooks", {}).items()):
    for group in groups:
        kept = []
        for hook in group.get("hooks", []):
            scripts = re.findall(r"(?:\$HOME|~|/[^\s\"']*?)/\.claude/[^\s\"']+", hook.get("command", ""))
            missing = [p for p in scripts if not os.path.exists(p.replace("$HOME", home).replace("~", home, 1))]
            (dropped if missing else kept).append(missing[0] if missing else hook)
        group["hooks"] = kept
    settings["hooks"][event] = [g for g in groups if g.get("hooks")]
    if not settings["hooks"][event]:
        del settings["hooks"][event]
json.dump(settings, open(path, "w"), indent=2)
for p in dropped:
    print(f"  [skip] hook {os.path.basename(p)} removed, its script is not in the vault")
EOF
    place "$filtered" "$HOME/.claude/settings.json"
    rm -rf "$(dirname "$filtered")"
fi

if want mcp; then
    say "MCP servers"
    tools=""
    { want claude || command -v claude >/dev/null; } && tools="claude"
    { want codex || [ -x "$HOME/.local/bin/codex" ] || command -v codex >/dev/null; } && tools="$tools,codex"
    { want opencode || [ -x "$HOME/.opencode/bin/opencode" ] || command -v opencode >/dev/null; } && tools="$tools,opencode"
    python3 - "$BUNDLE/mcp/servers.json" "$HOME/.claude.json" "${MCP_SERVERS_FILE:-}" "$tools" "$HOME" <<'EOF'
import getpass, json, os, re, sys
packaged, path, given_file, tools, home = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4].split(","), sys.argv[5]
applied = {}
config = json.load(open(path)) if os.path.exists(path) else {}
current = config.setdefault("mcpServers", {})

if given_file:
    # Complete definitions from the setup app: they win over what is configured.
    servers = json.load(open(given_file))
    os.remove(given_file)
    for name, srv in servers.items():
        current[name] = srv
        applied[name] = srv
        print(f"  [ok]   {name} ({srv.get('type', 'stdio')})")
    if not servers:
        print("  [skip] no servers chosen")
else:
    for name, srv in json.load(open(packaged)).items():
        if name in current:
            print(f"  [skip] {name} already configured")
            continue
        complete = True
        for section in ("headers", "env"):
            for key, value in (srv.get(section) or {}).items():
                if "{{SECRET}}" not in value:
                    continue
                try:
                    secret = getpass.getpass(f"  {name} {key}, empty = skip server: ")
                except EOFError:
                    secret = ""
                if not secret:
                    complete = False
                    break
                srv[section][key] = value.replace("{{SECRET}}", secret)
            if not complete:
                break
        if complete:
            current[name] = srv
            applied[name] = srv
            print(f"  [ok]   {name}")
        else:
            print(f"  [skip] {name} (no credential given)")
if "claude" in tools:
    json.dump(config, open(path, "w"), indent=2)
    os.chmod(path, 0o600)

def toml(value):
    return json.dumps(value, ensure_ascii=False)

def toml_key(name):
    return name if re.fullmatch(r"[A-Za-z0-9_-]+", name) else toml(name)

if "codex" in tools and applied:
    # Codex: ~/.codex/config.toml, one [mcp_servers.<name>] table per server; other tables stay.
    cpath = os.path.join(home, ".codex", "config.toml")
    os.makedirs(os.path.dirname(cpath), exist_ok=True)
    lines = open(cpath).read().splitlines() if os.path.exists(cpath) else []
    def replaced(header):
        h = header.strip()
        for n in applied:
            prefix = f"[mcp_servers.{toml_key(n)}"
            if h.startswith(prefix) and h[len(prefix):][:1] in ("]", "."):
                return True
        return False
    kept, skipping = [], False
    for line in lines:
        if line.lstrip().startswith("["):
            skipping = replaced(line)
        if not skipping:
            kept.append(line)
    for name, srv in applied.items():
        key = toml_key(name)
        kept += ["", f"[mcp_servers.{key}]"]
        if srv.get("type", "stdio") == "stdio":
            kept.append(f"command = {toml(srv.get('command', ''))}")
            kept.append(f"args = {toml(srv.get('args', []))}")
            if srv.get("env"):
                kept += [f"[mcp_servers.{key}.env]"] + [f"{toml_key(k)} = {toml(v)}" for k, v in srv["env"].items()]
        else:
            kept.append(f"url = {toml(srv.get('url', ''))}")
            if srv.get("headers"):
                kept += [f"[mcp_servers.{key}.http_headers]"] + [f"{toml_key(k)} = {toml(v)}" for k, v in srv["headers"].items()]
    open(cpath, "w").write("\n".join(kept).lstrip("\n") + "\n")
    os.chmod(cpath, 0o600)
    print(f"  [ok]   codex: {', '.join(applied)}")

if "opencode" in tools and applied:
    # OpenCode: "mcp" in ~/.config/opencode/opencode.json; local servers take the command as one list.
    opath = os.path.join(home, ".config", "opencode", "opencode.json")
    os.makedirs(os.path.dirname(opath), exist_ok=True)
    try:
        data = json.load(open(opath)) if os.path.exists(opath) else {"$schema": "https://opencode.ai/config.json"}
    except ValueError:
        data = None
        print("  [warn] opencode.json has comments (JSONC); add the MCP servers in the Agent Workbench app or by hand")
    if data is not None:
        mcp = data.setdefault("mcp", {})
        for name, srv in applied.items():
            if srv.get("type", "stdio") == "stdio":
                entry = {"type": "local", "command": [srv.get("command", "")] + list(srv.get("args", [])), "enabled": True}
                if srv.get("env"): entry["environment"] = srv["env"]
            else:
                entry = {"type": "remote", "url": srv.get("url", ""), "enabled": True}
                if srv.get("headers"): entry["headers"] = srv["headers"]
            mcp[name] = entry
        json.dump(data, open(opath, "w"), indent=2)
        os.chmod(opath, 0o600)
        print(f"  [ok]   opencode: {', '.join(applied)}")
EOF
fi

if want cron; then
    say "Cron jobs"
    if ! command -v crontab >/dev/null; then
        warn "crontab not available, install the cron package"
    elif [ -s "$BUNDLE/home/crontab.txt" ]; then
        current="$(crontab -l 2>/dev/null || true)"
        new="$current"
        while IFS= read -r line; do
            line="${line//"$OLD_HOME"/$HOME}"
            grep -qxF -- "$line" <<< "$current" && { skip "$line"; continue; }
            new="$new"$'\n'"$line"
            ok "$line"
        done < "$BUNDLE/home/crontab.txt"
        printf '%s\n' "$new" | sed '/^$/d' | crontab -
    else
        skip "none exported"
    fi
fi

if want git; then
    say "Git identity"
    if [ -z "$(git config --global user.name || true)" ] && [ -n "$GIT_NAME" ]; then
        git config --global user.name "${GIT_NAME_OVERRIDE:-$GIT_NAME}"
        git config --global user.email "${GIT_EMAIL_OVERRIDE:-$GIT_EMAIL}"
        ok "$(git config --global user.name) <$(git config --global user.email)>"
    else
        skip "already set or not exported"
    fi
fi

# ---------------------------------------------------------------------------
say "Status"
check() { if eval "$2" >/dev/null 2>&1; then ok "$1"; else warn "$1"; fi; }
want claude && check "claude on PATH"        "command -v claude"
want codex && check "codex installed"        "test -x $HOME/.local/bin/codex || command -v codex"
want opencode && check "opencode installed"  "test -x $HOME/.opencode/bin/opencode || command -v opencode"
want shell  && check "tmux on PATH"          "command -v tmux"
want shell  && check "starship on PATH"      "command -v starship"
want shell  && check "catppuccin tmux theme" "test -f $HOME/.tmux/plugins/tmux/catppuccin.tmux"
want config && check "agents linked"         "test -e $HOME/.claude/agents"
want config && check "hooks linked"          "test -e $HOME/.claude/hooks"
want config && check "settings.json valid"   "python3 -m json.tool $HOME/.claude/settings.json"
want vault  && check "vault memory index"    "test -f $VAULT/memory/MEMORY.md"

echo
[ $WARNINGS -gt 0 ] && echo "Finished with $WARNINGS warning(s), see above." || echo "Finished without warnings."
exit 0
