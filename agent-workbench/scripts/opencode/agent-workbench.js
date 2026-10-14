// Runs the vault's Claude Code hook scripts (scripts/hooks/hooks.json) inside OpenCode.
// OpenCode calls its tools read, edit, write, bash, grep and glob with camelCase
// arguments; the scripts expect Claude's payload, so every call is translated first.
// Linked to ~/.config/opencode/plugins/ by scripts/tool-hooks.py.
import { spawn, spawnSync } from "node:child_process"
import { existsSync, readFileSync, realpathSync } from "node:fs"
import { dirname, join } from "node:path"
import { fileURLToPath } from "node:url"

const SCRIPTS = dirname(dirname(realpathSync(fileURLToPath(import.meta.url))))
const HOOKS = join(SCRIPTS, "hooks")

const TOOLS = {
  read: (a) => ["Read", { file_path: a.filePath }],
  edit: (a) => ["Edit", { file_path: a.filePath, old_string: a.oldString, new_string: a.newString }],
  multiedit: (a) => ["Edit", { file_path: a.filePath }],
  write: (a) => ["Write", { file_path: a.filePath, content: a.content }],
  bash: (a) => ["Bash", { command: a.command }],
  grep: (a) => ["Grep", { pattern: a.pattern, path: a.path }],
  glob: (a) => ["Glob", { pattern: a.pattern, path: a.path }],
}

function load() {
  try {
    const spec = JSON.parse(readFileSync(join(HOOKS, "hooks.json"), "utf8"))
    for (const event of Object.keys(spec)) {
      if (event.startsWith("_")) { delete spec[event]; continue }
      spec[event] = spec[event]
        .filter((e) => existsSync(join(HOOKS, e.script)))
        .map((e) => ({ ...e, re: new RegExp(`^(${e.matcher})$`) }))
    }
    return spec
  } catch {
    return {}
  }
}

// A verdict in Claude's shape: exit code 2 with the reason on stderr, a PreToolUse
// "deny", or a "block" decision.
function verdict(result) {
  if (result.status === 2) return (result.stderr || "").trim() || "Blocked by a hook."
  try {
    const out = JSON.parse((result.stdout || "").trim() || "{}")
    const pre = out.hookSpecificOutput || {}
    if (pre.permissionDecision === "deny") return pre.permissionDecisionReason || "Blocked by a hook."
    if (out.decision === "block") return out.reason || "Blocked by a hook."
  } catch {}
  return null
}

export const AgentWorkbench = async ({ client, directory, worktree }) => {
  const spec = load()
  // Brings agents, commands and memory up to date, globally and for this repository; OpenCode
  // picks the result up on its next start.
  spawn("sh", ["-c", `python3 "$0"; python3 "$0" --project "$1"`, join(SCRIPTS, "tool-sync.py"), worktree || directory],
    { stdio: "ignore", detached: true }).unref()
  // The after hook is not documented to carry the arguments; keep them per call.
  const pending = new Map()

  async function agentType(sessionID) {
    try {
      const session = await client.session.get({ path: { id: sessionID } })
      return session?.data?.parentID ? "subagent" : undefined
    } catch {
      return undefined
    }
  }

  async function run(event, input, args) {
    const map = TOOLS[input.tool]
    const entries = spec[event] || []
    if (!map || entries.length === 0) return null
    const [toolName, toolInput] = map(args || {})
    const matching = entries.filter((e) => e.re.test(toolName))
    if (matching.length === 0) return null
    const payload = JSON.stringify({
      session_id: input.sessionID,
      cwd: directory,
      hook_event_name: event,
      tool_name: toolName,
      tool_input: toolInput,
      agent_type: await agentType(input.sessionID),
    })
    const reasons = []
    for (const entry of matching) {
      const script = join(HOOKS, entry.script)
      if (entry.async) {
        const child = spawn("python3", [script], { stdio: ["pipe", "ignore", "ignore"], detached: true })
        child.stdin.end(payload)
        child.unref()
        continue
      }
      const result = spawnSync("python3", [script], { input: payload, encoding: "utf8", timeout: (entry.timeout || 30) * 1000 })
      const reason = verdict(result)
      if (reason) reasons.push(reason)
    }
    return reasons.length ? reasons.join("\n") : null
  }

  return {
    "tool.execute.before": async (input, output) => {
      if (input.callID) pending.set(input.callID, output.args)
      const reason = await run("PreToolUse", input, output.args)
      if (reason) throw new Error(reason)
    },
    "tool.execute.after": async (input, output) => {
      const args = pending.get(input.callID) ?? input.args
      pending.delete(input.callID)
      const reason = await run("PostToolUse", input, args)
      if (reason) output.output = `${output.output ?? ""}\n\n${reason}`
    },
  }
}
