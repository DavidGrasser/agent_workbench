# Agent Workbench

One setup for Claude Code, Codex CLI and OpenCode on Windows with WSL, so the same
workflow, agents, commands, skills, hooks, MCP servers and long-term memory are
ready in every tool and on every PC.

- **Setup program** (Windows, one exe): pick the coding tools first, then only the
  options that fit them. Installs WSL if needed, the tools, tmux with a launcher
  and a Windows Terminal profile per tool, an Obsidian vault as long-term memory,
  MCP servers, hooks and a browser MCP (Playwright) for Codex and OpenCode.
- **Agent Workbench app** (stays in the start menu): overview, MCP servers per
  tool (also servers that only one agent can use), agents, projects, and every
  setting of each tool, generated from the tools' published config schemas.
- **One source, every tool**: the vault holds agents, commands and the memory
  index; `scripts/tool-sync.py` converts them into each tool's own format on every
  start. Hooks are written once (`scripts/hooks/hooks.json`) and run in all three.

## Layout

The repository has the shape of a vault, because the scripts find each other
relative to the vault root:

```
scripts/workflow-kit/      setup program: export.sh, install.sh, setup-app/ (WPF), templates/
scripts/tool-sync.py       agents, commands, skills and memory for Codex and OpenCode
scripts/tool-hooks.py      wires hooks.json into Codex (config.toml) and OpenCode (plugin)
scripts/secrets.py         secrets of MCP servers in ~/.config/agent-workbench/secrets.env
scripts/vault-link.sh      Claude Code SessionStart: memory links, global agents, tool sync
scripts/hooks/             hook scripts (git write guard, Obsidian graph pulse, Codex adapter)
scripts/opencode/          OpenCode plugin that runs the hooks
.obsidian/plugins/graph-pulse   Obsidian plugin: notes light up while an agent uses them
```

`scripts/workflow-kit/README.md` describes the setup in detail.

## Build the setup program

In WSL, with the .NET 8 SDK (`export.sh` installs it to `~/.dotnet` if missing):

```bash
scripts/workflow-kit/export.sh --generic ~/out
```

`--generic` builds a setup without any personal data: no vault content, no MCP
servers, no Git identity. Without `--generic`, run from inside your own vault, the
exe also carries your vault and your configuration (credentials are never packed;
the setup asks for them).

## Requirements

Windows 10 or 11 with WSL 2 (the setup installs it), Windows Terminal. The coding
tools need their own accounts or API keys; you sign in after the setup.
