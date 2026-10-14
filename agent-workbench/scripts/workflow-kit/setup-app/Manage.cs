using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ClaudeWorkflowSetup
{
    // Writes MCP servers through Claude's own CLI: ~/.claude.json is also Claude's state file,
    // and running sessions write it too.
    public static class ClaudeCli
    {
        public static Task<(int Code, string Output)> AddMcp(string distro, McpServer server) =>
            Run(distro, $"mcp add-json -s user {Shell.ShQuote(server.Name)} {Shell.ShQuote(new JavaScriptSerializer().Serialize(server.ToConfig()))}");

        public static Task<(int Code, string Output)> RemoveMcp(string distro, string name) =>
            Run(distro, $"mcp remove -s user {Shell.ShQuote(name)}");

        // The command line can carry a token, so it goes through a script file that is deleted right away.
        static Task<(int Code, string Output)> Run(string distro, string arguments) => Task.Run(() =>
        {
            Directory.CreateDirectory(Payload.WorkDir);
            var script = Path.Combine(Payload.WorkDir, $"cli-{Guid.NewGuid():N}.sh");
            Shell.WriteUnix(script, $"exec \"$HOME/.local/bin/claude\" {arguments}\n");
            try
            {
                return Shell.Capture("wsl.exe", $"-d {distro} -e sh \"{Wsl.ToLinux(distro, script)}\"", 60000);
            }
            finally
            {
                File.Delete(script);
            }
        });
    }

    // MCP servers of Codex (config.toml) and OpenCode (opencode.json), read and written in the shape the
    // rest of the app uses for Claude Code: {type, url, headers} or {type: stdio, command, args, env}.
    public static class ToolMcp
    {
        const string Script = @"
import json, os, re, shutil, sys, time, tomllib
tool, action, home = sys.argv[1], sys.argv[2], sys.argv[3]
arg = json.load(open(sys.argv[4])) if len(sys.argv) > 4 else {}
path = os.path.join(home, '.codex', 'config.toml') if tool == 'codex' else os.path.join(home, '.config', 'opencode', 'opencode.json')

def to_common(entry):
    if tool == 'codex':
        if 'url' in entry:
            return {'type': 'http', 'url': entry['url'], **({'headers': entry['http_headers']} if entry.get('http_headers') else {})}
        return {'type': 'stdio', 'command': entry.get('command', ''), 'args': entry.get('args', []), **({'env': entry['env']} if entry.get('env') else {})}
    if entry.get('type') == 'remote':
        return {'type': 'http', 'url': entry.get('url', ''), **({'headers': entry['headers']} if entry.get('headers') else {})}
    cmd = entry.get('command', [])
    cmd = cmd if isinstance(cmd, list) else [cmd]
    return {'type': 'stdio', 'command': cmd[0] if cmd else '', 'args': cmd[1:], **({'env': entry['environment']} if entry.get('environment') else {})}

def read_all():
    if not os.path.exists(path): return {}
    if tool == 'codex': return {k: to_common(v) for k, v in tomllib.load(open(path, 'rb')).get('mcp_servers', {}).items()}
    return {k: to_common(v) for k, v in json.load(open(path)).get('mcp', {}).items()}

if action == 'list':
    print(json.dumps(read_all())); sys.exit(0)

os.makedirs(os.path.dirname(path), exist_ok=True)
if os.path.exists(path): shutil.copy2(path, path + '.bak-' + time.strftime('%Y%m%d-%H%M%S'))
remove = set(arg.get('remove', []))
upsert = arg.get('set', {})

if tool == 'codex':
    def tk(k): return k if re.fullmatch(r'[A-Za-z0-9_-]+', k) else json.dumps(k)
    def tv(v): return json.dumps(v, ensure_ascii=False)
    lines = open(path).read().splitlines() if os.path.exists(path) else []
    gone = remove | set(upsert)
    def replaced(h):
        h = h.strip()
        return any(h.startswith(f'[mcp_servers.{tk(n)}') and h[len(f'[mcp_servers.{tk(n)}'):][:1] in (']', '.') for n in gone)
    kept, skipping = [], False
    for line in lines:
        if line.lstrip().startswith('['): skipping = replaced(line)
        if not skipping: kept.append(line)
    for name, srv in upsert.items():
        key = tk(name)
        kept += ['', f'[mcp_servers.{key}]']
        if srv.get('type', 'stdio') == 'stdio':
            kept += [f""command = {tv(srv.get('command', ''))}"", f""args = {tv(srv.get('args', []))}""]
            if srv.get('env'): kept += [f'[mcp_servers.{key}.env]'] + [f'{tk(k)} = {tv(v)}' for k, v in srv['env'].items()]
        else:
            kept.append(f""url = {tv(srv.get('url', ''))}"")
            if srv.get('headers'): kept += [f'[mcp_servers.{key}.http_headers]'] + [f'{tk(k)} = {tv(v)}' for k, v in srv['headers'].items()]
    open(path, 'w').write('\n'.join(kept).strip('\n') + '\n')
    tomllib.load(open(path, 'rb'))
else:
    data = json.load(open(path)) if os.path.exists(path) else {'$schema': 'https://opencode.ai/config.json'}
    mcp = data.setdefault('mcp', {})
    for name in remove: mcp.pop(name, None)
    for name, srv in upsert.items():
        if srv.get('type', 'stdio') == 'stdio':
            entry = {'type': 'local', 'command': [srv.get('command', '')] + list(srv.get('args', [])), 'enabled': True}
            if srv.get('env'): entry['environment'] = srv['env']
        else:
            entry = {'type': 'remote', 'url': srv.get('url', ''), 'enabled': True}
            if srv.get('headers'): entry['headers'] = srv['headers']
        mcp[name] = entry
    json.dump(data, open(path, 'w'), indent=2)
os.chmod(path, 0o600)
print('saved')
";

        static (int Code, string Output) Run(string distro, string tool, string action, object argument)
        {
            Directory.CreateDirectory(Payload.WorkDir);
            var script = Path.Combine(Payload.WorkDir, $"mcp-{Guid.NewGuid():N}.py");
            var argFile = Path.Combine(Payload.WorkDir, $"mcp-{Guid.NewGuid():N}.json");
            Shell.WriteUnix(script, Script);
            File.WriteAllText(argFile, new JavaScriptSerializer().Serialize(argument ?? new Dictionary<string, object>()), Shell.Utf8);
            try
            {
                return Shell.Capture("wsl.exe", $"-d {distro} -e python3 \"{Wsl.ToLinux(distro, script)}\" {tool} {action} \"{Wsl.Home(distro)}\" \"{Wsl.ToLinux(distro, argFile)}\"");
            }
            finally
            {
                File.Delete(script);
                File.Delete(argFile);
            }
        }

        public static List<McpServer> List(string distro, string tool)
        {
            var (code, output) = Run(distro, tool, "list", null);
            if (code != 0) throw new InvalidOperationException(output);
            var all = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, Dictionary<string, object>>>(output.Trim());
            return all.Select(kv => McpServer.Parse(kv.Key, kv.Value)).ToList();
        }

        public static Task<(int Code, string Output)> Save(string distro, string tool, IEnumerable<McpServer> servers, IEnumerable<string> remove) =>
            Task.Run(() => Run(distro, tool, "write", new Dictionary<string, object>
            {
                ["set"] = servers.ToDictionary(s => s.Name, s => (object)s.ToConfig()),
                ["remove"] = remove.ToList(),
            }));
    }

    public static class McpClient
    {
        public sealed class Tool
        {
            public string Name;
            public string Description;
        }

        public static async Task<List<Tool>> ListTools(McpServer server)
        {
            if (server.Type != "http") throw new InvalidOperationException("Loading tools works for HTTP servers only.");
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
            {
                string session = null;
                async Task<Dictionary<string, object>> Call(string method, int? id, object parameters)
                {
                    var body = new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["method"] = method };
                    if (id != null) body["id"] = id;
                    if (parameters != null) body["params"] = parameters;
                    using (var request = new HttpRequestMessage(HttpMethod.Post, server.Url))
                    {
                        if (server.Header.Trim() != "" && server.Secret != "")
                            request.Headers.TryAddWithoutValidation(server.Header.Trim(), server.AuthValue);
                        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
                        if (session != null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
                        request.Content = new StringContent(new JavaScriptSerializer().Serialize(body), Encoding.UTF8, "application/json");
                        using (var response = await http.SendAsync(request))
                        {
                            if (response.Headers.TryGetValues("Mcp-Session-Id", out var values)) session = values.First();
                            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"The server answers {method} with {(int)response.StatusCode}.");
                            if (id == null) return null;
                            var text = await response.Content.ReadAsStringAsync();
                            // Streamable HTTP may answer as an event stream: the JSON sits in the data lines.
                            var json = text.TrimStart().StartsWith("{") ? text
                                : string.Join("", text.Split('\n').Where(l => l.StartsWith("data:")).Select(l => l.Substring(5).Trim()));
                            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(json);
                        }
                    }
                }

                await Call("initialize", 1, new Dictionary<string, object>
                {
                    ["protocolVersion"] = "2025-03-26",
                    ["capabilities"] = new Dictionary<string, object>(),
                    ["clientInfo"] = new Dictionary<string, object> { ["name"] = "agent-workbench", ["version"] = "1.0" },
                });
                await Call("notifications/initialized", null, null);
                var result = await Call("tools/list", 2, new Dictionary<string, object>());
                if (!(result != null && result.TryGetValue("result", out var r) && r is Dictionary<string, object> res
                      && res.TryGetValue("tools", out var t) && t is System.Collections.ArrayList tools))
                    throw new InvalidOperationException("The server returned no tool list.");
                return tools.OfType<Dictionary<string, object>>()
                    .Select(x => new Tool { Name = x["name"] as string, Description = x.TryGetValue("description", out var d) ? d as string ?? "" : "" })
                    .OrderBy(x => x.Name).ToList();
            }
        }
    }

    // An agent definition. Only description, model and tools are ever rewritten, every other
    // byte of the file stays as it was.
    public sealed class AgentFile
    {
        public static readonly string[] BuiltInTools =
            { "Read", "Write", "Edit", "Grep", "Glob", "Bash", "Agent", "WebSearch", "WebFetch", "NotebookEdit", "TodoWrite", "Skill" };

        public string Path;
        public string Name;
        public string Description = "";
        public string Model = "";
        public List<string> Tools;
        public bool DescriptionEditable = true;
        public string Body = "";
        // MCP servers defined in the agent itself (frontmatter mcpServers, one "- name: {json}" line
        // each): only this subagent gets them, the main conversation never sees them.
        public Dictionary<string, string> McpServers = new Dictionary<string, string>();

        public static AgentFile Load(string path)
        {
            var agent = new AgentFile { Path = path, Name = System.IO.Path.GetFileNameWithoutExtension(path) };
            var lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n').ToList();
            var (start, end) = FrontMatter(lines);
            if (start < 0) { agent.Body = string.Join("\n", lines); return agent; }
            for (int i = start + 1; i < end; i++)
            {
                var m = Regex.Match(lines[i], "^([A-Za-z_][\\w-]*):\\s*(.*)$");
                if (!m.Success) continue;
                var key = m.Groups[1].Value;
                var value = m.Groups[2].Value.Trim();
                var continuation = Continuation(lines, i, end);
                if (key == "name") agent.Name = Unquote(value);
                else if (key == "model") agent.Model = Unquote(value);
                else if (key == "description")
                {
                    agent.DescriptionEditable = continuation.Count == 0 && value != "|" && value != ">";
                    agent.Description = agent.DescriptionEditable ? Unquote(value) : string.Join(" ", continuation.Select(l => l.Trim()));
                }
                else if (key == "mcpServers")
                {
                    foreach (var item in continuation)
                    {
                        var s = Regex.Match(item, "^\\s*-\\s*([A-Za-z0-9_.-]+):\\s*(\\{.*\\})\\s*$");
                        if (s.Success) agent.McpServers[s.Groups[1].Value] = s.Groups[2].Value;
                    }
                }
                else if (key == "tools")
                {
                    agent.Tools = continuation.Count > 0
                        ? continuation.Select(l => l.Trim().TrimStart('-').Trim()).Where(l => l != "").ToList()
                        : value.Trim('[', ']').Split(',').Select(x => Unquote(x.Trim())).Where(x => x != "").ToList();
                }
            }
            agent.Body = string.Join("\n", lines.Skip(end + 1));
            return agent;
        }

        public static string Create(string folder, string name, string description, string model, List<string> tools, string body,
            Dictionary<string, string> mcpServers = null)
        {
            var path = System.IO.Path.Combine(folder, name + ".md");
            var text = new StringBuilder("---\n");
            text.Append($"name: {name}\n");
            text.Append($"description: {Yaml(description)}\n");
            if (tools != null) text.Append($"tools: {string.Join(", ", tools)}\n");
            if (!string.IsNullOrEmpty(model)) text.Append($"model: {model}\n");
            if (mcpServers != null && mcpServers.Count > 0)
            {
                text.Append("mcpServers:\n");
                foreach (var kv in mcpServers) text.Append($"  - {kv.Key}: {kv.Value}\n");
            }
            text.Append("---\n\n").Append(body.Replace("\r\n", "\n").TrimEnd()).Append("\n");
            File.WriteAllText(path, text.ToString(), Shell.Utf8);
            return path;
        }

        public void Save(string description, string model, List<string> tools)
        {
            var raw = File.ReadAllText(Path);
            var newline = raw.Contains("\r\n") ? "\r\n" : "\n";
            var lines = raw.Replace("\r\n", "\n").Split('\n').ToList();
            var (start, end) = FrontMatter(lines);
            if (start < 0)
            {
                lines.InsertRange(0, new[] { "---", $"name: {Name}", "---", "" });
                (start, end) = (0, 2);
            }
            if (DescriptionEditable && description != Description) end = SetKey(lines, start, end, "description", Yaml(description));
            if ((model ?? "") != Model) end = SetKey(lines, start, end, "model", string.IsNullOrEmpty(model) ? null : model);
            bool toolsChanged = (tools == null) != (Tools == null) || (tools != null && !tools.SequenceEqual(Tools));
            if (toolsChanged) SetKey(lines, start, end, "tools", tools == null ? null : string.Join(", ", tools));
            File.WriteAllText(Path, string.Join(newline, lines), Shell.Utf8);
        }

        static (int, int) FrontMatter(List<string> lines)
        {
            if (lines.Count == 0 || lines[0].Trim() != "---") return (-1, -1);
            int end = lines.FindIndex(1, l => l.Trim() == "---");
            return end < 0 ? (-1, -1) : (0, end);
        }

        static List<string> Continuation(List<string> lines, int keyLine, int end)
        {
            var result = new List<string>();
            for (int j = keyLine + 1; j < end && (lines[j].StartsWith(" ") || lines[j].StartsWith("\t")); j++) result.Add(lines[j]);
            return result;
        }

        // Replaces, removes or appends one key inside the front matter and returns the new end index.
        static int SetKey(List<string> lines, int start, int end, string key, string value)
        {
            int index = -1;
            for (int i = start + 1; i < end; i++)
                if (Regex.IsMatch(lines[i], $"^{Regex.Escape(key)}:")) { index = i; break; }
            if (index >= 0)
            {
                int extra = Continuation(lines, index, end).Count;
                lines.RemoveRange(index, 1 + extra);
                end -= 1 + extra;
                if (value == null) return end;
                lines.Insert(index, $"{key}: {value}");
                return end + 1;
            }
            if (value == null) return end;
            lines.Insert(end, $"{key}: {value}");
            return end + 1;
        }

        static string Unquote(string s) => s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[s.Length - 1] == s[0] ? s.Substring(1, s.Length - 2) : s;

        static string Yaml(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            bool plain = s != "" && !Regex.IsMatch(s, "^[\\[\\]{}>|*&!%@`'\"#,?:-]|: | #");
            return plain ? s : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
