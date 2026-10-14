# Agent Workbench

`Agent-Workbench-Setup-<date>.exe` sets up Claude Code, Codex CLI and OpenCode,
one or more, on a Windows PC with WSL: the tools themselves, tmux, Starship, a
launcher and a Windows Terminal profile per tool, the context menu "Open in
Claude Code", the Nerd Font, Obsidian, MCP servers and an Obsidian vault that
holds memory, shared instructions and agents. Credentials are never part of the package.

After the first run the program stays installed as **Agent Workbench** (start
menu, `%LOCALAPPDATA%\Programs\AgentWorkbench`). Opened from there it manages
what the coding tools use on this PC. Until 13.10.2026 it was called Claude
Configuration; the next setup run takes over its settings and removes it:

- **Overview:** the vault, its agents and the configured MCP servers.
- **MCP servers:** edit, add and remove user-scope servers (HTTP, SSE, stdio),
  test HTTP servers. Changes go through `claude mcp add-json` and
  `claude mcp remove`, never by rewriting `~/.claude.json`, because running
  Claude sessions write that file too.
- **Agents:** edit description, model and allowed tools of every agent in the
  vault, or create a new one. Tools of an HTTP MCP server can be loaded live and
  ticked one by one. Only the `description`, `model` and `tools` lines of an
  agent file are rewritten, and only when they changed; everything else stays
  byte for byte.

- **Projects:** one project per top-level repository, with the repositories
  inside it as part of it. Creating a project (suggested from the repositories
  Claude has been used in) writes a note `projects/<name>.md` with the git
  `remote:` and an agent set `project-agents/<name>/` into the vault; where the
  repository lives on a PC is stored by the app on that PC, so on another PC the
  project shows "Not on this PC yet" with "Locate folder". Per project it shows which agents apply (global, defined in the
  repository, or personal), which MCP servers (global, this folder only,
  `.mcp.json`) and which `CLAUDE.md` files. Personal agent sets from
  `project-agents/` are linked into the project with one click: symlinks in
  `.claude/agents/`, each listed in `.git/info/exclude`, so they never reach the
  repository. Nested repositories (Claude only looks up to the root of the
  repository it starts in) can be linked in the same step, and global agents can
  be moved into a set.

- **Settings:** every setting of Claude Code's `settings.json`, generated from
  the published schema (`setup-app/settings-schema.json`, copied from
  json.schemastore.org/claude-code-settings.json; refresh it by replacing the
  file). Fields match their type: Default/On/Off, choices, text, numbers, lists,
  `NAME=value` for `env`, JSON for objects such as `hooks`. Scope is the user
  (`~/.claude/settings.json`) or one project (`.claude/settings.local.json`, kept
  out of git through `.git/info/exclude`). Saving writes only the changed keys,
  keeps order, formatting and unknown keys, and leaves a `.bak-<timestamp>` copy.
  Settings marked "managed settings only" are hidden.

Changes apply to new Claude sessions.

## Running the setup

1. Double-click the exe. Windows warns on the first start because the file is not
   signed: "More info", then "Run anyway".
2. **Coding tools:** Claude Code, Codex CLI, OpenCode, one or more. The setup
   checks the PC first; a tool already installed is preselected and only set up,
   not installed again.
3. **Options:** only the options that work with the chosen tools. Claude
   configuration, the Chrome extension and the /insights cron job appear with
   Claude Code only. Anything already installed stays unselected, select it to
   set it up again. Dependencies are selected automatically; a dependency that is
   already installed counts as satisfied. When a tool is new, Windows integration
   and MCP servers are preselected, since both are written per tool.
4. **Settings:** WSL distribution, vault, Git identity and MCP servers.
   - Vault: *Restore from this package* (only when the package contains a vault),
     *Start a new vault* (layout, Obsidian plugins and generic hooks), or *Use an
     existing folder*, for example a vault copied from the old PC. The setup shows
     what it found in the folder.
   - MCP servers: servers from the package are prefilled. Edit, remove or add
     HTTP, SSE or local (stdio) servers. "Test" checks an HTTP server and its token.
5. **WSL:** if WSL is missing, the setup installs Ubuntu (administrator rights). If
   Windows needs a restart, the setup continues on its own afterwards. A missing
   Linux user is created in the setup.
6. **Done:** sign in to each tool, add the Chrome extension, open Obsidian and trust
   its plugins.

User names and paths may differ on the new PC, the setup rewrites the old paths.
Running it again is safe: existing files are kept as `*.bak-<timestamp>`, a vault
folder that is not empty is never overwritten. Logs are in
`%LOCALAPPDATA%\AgentWorkbench\Setup`.

## Vault layout

The setup links whatever the vault provides into `~/.claude`. Any vault that
follows this layout works, so everybody can bring their own agents and workflow.

| In the vault | Linked to | Meaning |
|---|---|---|
| `agents/*.md` | `~/.claude/agents`, one link per file | subagents, available in every project |
| `commands/*.md` | `~/.claude/commands` | slash commands |
| `scripts/hooks/` | `~/.claude/hooks` | hook scripts referenced in `settings.json` |
| `scripts/bin/` | `~/.claude/bin` | helper programs |
| `workflow/CLAUDE.md` | `~/.claude/CLAUDE.md` | how Claude works: routes, team, review, verification |
| `scripts/vault-link.sh` | `~/.claude/vault-link.sh` | links every project's auto-memory to `memory/` |
| the vault itself | `~/.claude/vault` | stable path for agents and the workflow to refer to |
| `memory/` | every `~/.claude/projects/*/memory` | Claude's auto-memory |
| `project-agents/<set>/*.md` | `<repo>/.claude/agents/` per project, via the app | agents for one project only, never committed; global on a PC that lists the set in `~/.claude/global-agent-sets` |

## Generic coding workflow

The setup can add a generic team workflow to the vault: the agents `coder`,
`reviewer`, `verifier`, `tracer` and `ui-inspector` plus `workflow/CLAUDE.md`
(choose a route, code, review in at most three rounds, verify in two gates,
never commit before the user tested). It is bound to no language or framework;
project specifics come from the project's own `CLAUDE.md` and tooling. Files that
already exist in the vault are never overwritten. By default it is offered only
for a vault without agents, so a vault with its own workflow keeps it.

A new vault is initialised as a git repository with a first commit, because its
obsidian-git plugin expects one; the Obsidian component installs Git for Windows
when it is missing. Claude starts with `--chrome` only when the Chrome extension is
selected or already installed.

`vault-link.sh` (SessionStart hook) keeps `~/.claude/agents` in sync: one link per
agent in `agents/` and in every set this PC lists in `~/.claude/global-agent-sets`.
That list lives outside the vault, so the same vault can make a set global on one
PC and project-only on another. The script and `graph_pulse.py` locate the vault
from their own path, so a vault copied anywhere keeps working.

A hook in `settings.json` whose script is not in the vault is removed during the
setup, so a vault without it does not break every tool call.

The personal `settings.json` of the exporting PC is installed only together with
the packaged vault; the workflow itself travels inside the vault as
`workflow/CLAUDE.md`. A new or foreign vault gets the
generic `settings.json` from `templates/`.

## Codex and OpenCode

Besides Claude Code the setup installs Codex CLI and OpenCode on request (both
opt-in, each through its official install script). All tools share one
instruction core, `workflow/AGENTS.md` in the vault: Claude Code imports it at
the top of `workflow/CLAUDE.md` (`@~/.claude/vault/workflow/AGENTS.md`) and adds
its team mechanics, Codex links it as `~/.codex/AGENTS.md`, OpenCode as
`~/.config/opencode/AGENTS.md` (without that link OpenCode would fall back to
`~/.claude/CLAUDE.md`). MCP servers chosen in the setup are written into every
installed tool: `~/.claude.json`, `[mcp_servers.<name>]` in `~/.codex/config.toml`
and `"mcp"` in `~/.config/opencode/opencode.json`; other entries stay. Each tool
gets a tmux launcher (`~/codex-tmux.sh`, `~/opencode-tmux.sh`) and its own
Windows Terminal profile. Agent teams in tmux panes and auto-memory exist in
Claude Code only.

`scripts/tool-sync.py` keeps Codex and OpenCode in step with the vault, for
each tool that is installed, and only touches files it generated itself:

| | OpenCode | Codex |
|---|---|---|
| agents (`agents/` plus the sets in `~/.claude/global-agent-sets`) | `~/.config/opencode/agents/*.md`, `mode: subagent`, `permission` from the tool list | `~/.codex/agents/*.toml`, `developer_instructions`; no shell when the tool list lacks Bash; an agent with its own MCP server is skipped |
| commands (`commands/*.md`) | `~/.config/opencode/commands/`, same template syntax | skills in `~/.agents/skills/<name>/`, invoked as `$name` (Codex has no custom commands any more) |
| skills (`~/.claude/skills`) | read directly | linked into `~/.agents/skills` |
| memory index | `instructions` in `opencode.json` | SessionStart hook `session_context.py` |
| agent-only MCP servers | in `mcp`, tools off globally, on for the agent | not possible |

Models are left out, so an agent runs on whatever provider the tool is connected
to. `--project <repo>` converts the vault links in `<repo>/.claude/agents` into
`.opencode/agents` and `.codex/agents`, excluded from git. It runs from
`vault-link.sh` (Claude Code start), the Codex SessionStart hook (global and the
current repository), the OpenCode plugin (same) and Agent Workbench when it links
a set. Verified 14.10.2026 with real OpenCode 1.19 (agents, commands, skills,
memory in context, agent-only server allowed only for its agent) and Codex
0.162 (config parsed, files written; agents not yet seen in a signed-in session).

Secrets of agent-only MCP servers live in `~/.config/agent-workbench/secrets.env`
(written by `scripts/secrets.py`, value on stdin), loaded by `~/.bashrc` and
`~/.profile`, so all tools resolve `${NAME}` or `{env:NAME}` alike. Running tmux
sessions pick a new secret up only after a restart.

Hooks: `scripts/hooks/hooks.json` lists the hook scripts in Claude Code's event
and tool names; Claude Code itself keeps its registration in `settings.json`.
`scripts/tool-hooks.py codex` writes a marked `[[hooks.*]]` block at the end of
`~/.codex/config.toml`, each script run through `scripts/hooks/codex_hook.py`,
which splits an `apply_patch` into one Edit or Write per file. Codex runs new
or changed hooks only after they were trusted once with `/hooks`.
`scripts/tool-hooks.py opencode` links the plugin
`scripts/opencode/agent-workbench.js` into `~/.config/opencode/plugins/`; it
translates OpenCode's tool calls into Claude's payload, blocks on a deny, and
appends a PostToolUse block reason to the tool output. A subagent session (one
with a parent) is reported as `agent_type: "subagent"`, so `git_write_guard.py`
stops it from committing. Verified with real OpenCode 1.19: edit guard,
subagent commit blocked, graph pulse. Codex: config parsed by `codex doctor`,
hook firing not yet verified because it needs a signed-in Codex.

With Windows integration, every selected tool gets a folder context menu entry
"Open in Claude Code / Codex / OpenCode"; the Codex and OpenCode entries open
`wt.exe -p "<Tool>"` with the tool's tmux launcher and the folder.

The option "Browser for Codex and OpenCode" adds Playwright MCP
(`npx -y @playwright/mcp@latest --browser chrome`) to both tools. The setup
installs Node.js 22 from NodeSource when there is no Node.js 20 or newer
(Playwright needs 20, Ubuntu 24.04 ships 18) and Google Chrome through
`npx playwright install --with-deps chrome`, both as root; the window shows
on the Windows desktop through WSLg. Claude Code keeps Claude in Chrome.

In Agent Workbench the MCP servers and Settings pages show one chip per
installed tool (none when only Claude Code is there). Codex servers are read
and written as `[mcp_servers.<name>]` in `config.toml` (TOML read through
`tomllib`), OpenCode servers as `"mcp"` in `opencode.json`, each with a backup;
"Copy to …" puts a server into another installed tool. OpenCode also reads an
`opencode.jsonc` it creates on its first start; the app only edits
`opencode.json`. For testing, `AGENT_WORKBENCH_HOME` points the app at
another Linux home.

## Building a new setup

In WSL:

```bash
/mnt/c/Users/<windows-user>/Documents/claude-vault/scripts/workflow-kit/export.sh
```

Writes the exe to the Windows desktop. Options:

- `--generic` builds a setup others can use: no personal vault, MCP servers,
  Git identity, `CLAUDE.md`, `settings.json` or own skills (`--no-vault` is the
  old name).
- `--folder` also writes the unpacked package, installable without the exe via
  `install.bat`.
  It takes tmux, Starship, the launchers, the crontab line, the context menu
  script and the terminal profile from `templates/home` and `templates/windows`,
  written for the neutral user `agent-workbench-user`, so it builds on a PC
  without Claude Code and carries nothing of the machine it was built on.

A missing .NET SDK is installed once to `~/.dotnet`. The export aborts if a token
ended up in the package.

## Files

| File | Role |
|---|---|
| `export.sh` | collects everything, strips tokens, builds the exe |
| `install.sh` | Linux part inside WSL, switched by `COMPONENTS` and `VAULT_SOURCE` |
| `setup-app/` | WPF app (.NET Framework 4.8, present on every Windows 10/11) |
| `templates/` | generic `settings.json`, the new-vault template and the generic workflow (`*.tmpl` become `*.md`) |
| `install.bat`, `install.ps1` | console route without the exe |
