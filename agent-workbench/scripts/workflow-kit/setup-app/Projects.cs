using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ClaudeWorkflowSetup
{
    public sealed class ProjectAgent
    {
        public string Name;
        public string Folder;
        public string LinkTarget;
        public bool IsPersonal;
    }

    // A project as the vault knows it: a note in projects/ that carries the git remote of its repository.
    public sealed class VaultProject
    {
        public string Name;
        public string Title;
        public string Remote;
        public string Status;
        public string NoteFile;
    }

    public sealed class ProjectInfo
    {
        public string Path;
        public string Root;
        public List<ProjectAgent> Agents = new List<ProjectAgent>();
        public List<string> Instructions = new List<string>();
        // Windows cannot follow a Linux symlink through \\wsl.localhost, so files are opened at their real path.
        public Dictionary<string, string> RealPaths = new Dictionary<string, string>();
        public List<string> McpJsonFiles = new List<string>();
        public List<McpServer> LocalMcp = new List<McpServer>();
        public List<string> ProjectMcp = new List<string>();

        public string Name => System.IO.Path.GetFileName(Root.TrimEnd('/'));
    }

    // Projects are folders Claude was started in. Claude reads .claude/agents from the start
    // folder up to the repository root, and CLAUDE.md files up to /.
    public static class Projects
    {
        public const string PersonalFolder = "project-agents";

        const string Probe = @"
p=""$1""; cd ""$p"" 2>/dev/null || exit 3
root=""$(git rev-parse --show-toplevel 2>/dev/null || pwd)""
echo ""root=$root""
d=""$p""; agents=1
while :; do
  if [ $agents = 1 ]; then
    for f in ""$d""/.claude/agents/*.md; do
      [ -e ""$f"" ] || [ -L ""$f"" ] || continue
      echo ""agent=$d|$(basename ""$f"")|$( [ -L ""$f"" ] && readlink -f ""$f"")""
    done
  fi
  for c in CLAUDE.md CLAUDE.local.md .claude/CLAUDE.md; do [ -f ""$d/$c"" ] && echo ""claude=$d/$c|$(readlink -f ""$d/$c"")""; done
  [ -f ""$d/.mcp.json"" ] && echo ""mcpjson=$d/.mcp.json""
  [ ""$d"" = ""$root"" ] && agents=0
  [ ""$d"" = / ] && break
  d=""$(dirname ""$d"")""
done
for c in ""$HOME/.claude/CLAUDE.md""; do [ -f ""$c"" ] && echo ""claude=$c|$(readlink -f ""$c"")""; done
exit 0
";

        public static string WindowsPath(string distro, string linuxPath)
        {
            var m = Regex.Match(linuxPath, "^/mnt/([a-z])(/.*)?$");
            return m.Success ? $"{m.Groups[1].Value.ToUpperInvariant()}:{(m.Groups[2].Value == "" ? "\\" : m.Groups[2].Value.Replace('/', '\\'))}" : Wsl.UncPath(distro, linuxPath);
        }

        static Dictionary<string, object> ClaudeJson(string distro)
        {
            var file = Wsl.UncPath(distro, Wsl.Home(distro) + "/.claude.json");
            return File.Exists(file)
                ? new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(File.ReadAllText(file))
                : new Dictionary<string, object>();
        }

        // Folders Claude has been started in that still exist, plus the ones added by hand.
        public static List<string> Known(string distro, IEnumerable<string> extra)
        {
            var paths = new List<string>();
            if (ClaudeJson(distro).TryGetValue("projects", out var p) && p is Dictionary<string, object> projects) paths.AddRange(projects.Keys);
            paths.AddRange(extra ?? Enumerable.Empty<string>());
            return paths.Distinct().Where(path => !path.StartsWith("/tmp/") && Directory.Exists(WindowsPath(distro, path))).OrderBy(x => x).ToList();
        }

        public static ProjectInfo Inspect(string distro, string path, string vaultLinux)
        {
            var info = new ProjectInfo { Path = path, Root = path };
            var output = RunScript(distro, Probe, WinArg(path)).Output;
            foreach (var line in output.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                var key = line.Substring(0, eq);
                var value = line.Substring(eq + 1).Trim();
                if (key == "root") info.Root = value;
                else if (key == "claude")
                {
                    var parts = value.Split('|');
                    info.Instructions.Add(parts[0]);
                    info.RealPaths[parts[0]] = parts.Length > 1 && parts[1] != "" ? parts[1] : parts[0];
                }
                else if (key == "mcpjson") info.McpJsonFiles.Add(value);
                else if (key == "agent")
                {
                    var parts = value.Split('|');
                    if (parts.Length < 3) continue;
                    info.Agents.Add(new ProjectAgent
                    {
                        Folder = parts[0],
                        Name = System.IO.Path.GetFileNameWithoutExtension(parts[1]),
                        LinkTarget = parts[2],
                        IsPersonal = parts[2] != "" && vaultLinux != null && parts[2].StartsWith(vaultLinux.TrimEnd('/') + "/" + PersonalFolder + "/"),
                    });
                }
            }

            var config = ClaudeJson(distro);
            if (config.TryGetValue("projects", out var p) && p is Dictionary<string, object> projects
                && projects.TryGetValue(path, out var e) && e is Dictionary<string, object> entry
                && entry.TryGetValue("mcpServers", out var m) && m is Dictionary<string, object> local)
                info.LocalMcp = local.Where(kv => kv.Value is Dictionary<string, object>)
                    .Select(kv => McpServer.Parse(kv.Key, (Dictionary<string, object>)kv.Value)).ToList();

            foreach (var file in info.McpJsonFiles)
            {
                try
                {
                    var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(WindowsPath(distro, file)));
                    if (json.TryGetValue("mcpServers", out var s) && s is Dictionary<string, object> servers) info.ProjectMcp.AddRange(servers.Keys);
                }
                catch (Exception) { }
            }
            return info;
        }

        // Links a personal agent from the vault into <root>/.claude/agents and hides the link from git
        // through .git/info/exclude, which is local and never committed.
        public static Task<(int Code, string Output)> Link(string distro, string root, string agentFileLinux) => Task.Run(() => RunScript(distro, @"
root=""$1""; file=""$2""; name=""$(basename ""$file"")""
mkdir -p ""$root/.claude/agents"" || exit 1
ln -sfn ""$file"" ""$root/.claude/agents/$name"" || exit 1
common=""$(git -C ""$root"" rev-parse --git-common-dir 2>/dev/null)"" || exit 0
case ""$common"" in /*) ;; *) common=""$root/$common"" ;; esac
mkdir -p ""$common/info""
grep -qxF ""/.claude/agents/$name"" ""$common/info/exclude"" 2>/dev/null || echo ""/.claude/agents/$name"" >> ""$common/info/exclude""
" + OpenCodeSync, WinArg(root) + " " + WinArg(agentFileLinux)));

        // Codex and OpenCode read their own agent formats: the vault's tool-sync.py mirrors the
        // repository's links into .codex/agents and .opencode/agents. $file is the agent inside
        // <vault>/project-agents/<set>/.
        const string OpenCodeSync = @"
conv=""$(dirname ""$(dirname ""$(dirname ""$(readlink -f ""$file"")"")"")"")/scripts/tool-sync.py""
if [ -f ""$conv"" ] && { [ -d ""$HOME/.config/opencode"" ] || [ -d ""$HOME/.codex"" ]; }; then python3 ""$conv"" --project ""$root""; fi
exit 0
";

        public static Task<(int Code, string Output)> Unlink(string distro, string root, string name) => Task.Run(() => RunScript(distro, @"
root=""$1""; name=""$2.md""; link=""$root/.claude/agents/$name""
[ -L ""$link"" ] || { echo ""not a link: $link""; exit 1; }
file=""$(readlink -f ""$link"")""
rm ""$link""
common=""$(git -C ""$root"" rev-parse --git-common-dir 2>/dev/null)""
if [ -n ""$common"" ]; then
  case ""$common"" in /*) ;; *) common=""$root/$common"" ;; esac
  [ -f ""$common/info/exclude"" ] && sed -i ""\#^/.claude/agents/$name\$#d"" ""$common/info/exclude""
fi
" + OpenCodeSync, WinArg(root) + " " + WinArg(name)));

        // Repositories below the root, such as the addon repositories of an Odoo checkout: Claude stops
        // at the root of the repository it starts in, so links in the outer root do not reach them.
        public static List<string> NestedRepos(string distro, string root) =>
            RunScript(distro, @"find ""$1"" -mindepth 2 -maxdepth 6 \( -name node_modules -o -name .venv \) -prune -o -name .git -print -prune 2>/dev/null | sed 's#/\.git$##' | sort", WinArg(root))
                .Output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("/")).ToList();

        // Every folder in <vault>/project-agents is one set of personal agents.
        public static Dictionary<string, List<string>> Sets(string vaultWindows)
        {
            var folder = System.IO.Path.Combine(vaultWindows ?? "", PersonalFolder);
            return Directory.Exists(folder)
                ? Directory.GetDirectories(folder).OrderBy(d => d).ToDictionary(System.IO.Path.GetFileName,
                    d => Directory.GetFiles(d, "*.md").Select(System.IO.Path.GetFileNameWithoutExtension).OrderBy(x => x).ToList())
                : new Dictionary<string, List<string>>();
        }

        public static List<VaultProject> FromVault(string vaultWindows)
        {
            var folder = System.IO.Path.Combine(vaultWindows ?? "", "projects");
            var result = new List<VaultProject>();
            if (!Directory.Exists(folder)) return result;
            foreach (var file in Directory.GetFiles(folder, "*.md"))
            {
                var front = Regex.Match(File.ReadAllText(file).Replace("\r\n", "\n"), "^---\\s*\\n(.*?)\\n---", RegexOptions.Singleline);
                if (!front.Success) continue;
                string Field(string key)
                {
                    var m = Regex.Match(front.Groups[1].Value, $"^{key}:[ \\t]*(.*)$", RegexOptions.Multiline);
                    return m.Success ? m.Groups[1].Value.Trim().Trim('"', '\'') : "";
                }
                if (Field("type") != "project" || Field("remote") == "") continue;
                result.Add(new VaultProject
                {
                    Name = System.IO.Path.GetFileNameWithoutExtension(file),
                    Title = Field("title"),
                    Remote = Field("remote"),
                    Status = Field("status"),
                    NoteFile = file,
                });
            }
            return result.OrderBy(p => p.Name).ToList();
        }

        // The outermost repositories among the folders Claude has been started in: candidates for a new project.
        public static List<(string Root, string Remote)> SuggestRepos(string distro, IEnumerable<string> folders)
        {
            var list = folders.ToList();
            if (list.Count == 0) return new List<(string, string)>();
            var output = RunScript(distro, @"
for p in ""$@""; do
  r=""$(git -C ""$p"" rev-parse --show-toplevel 2>/dev/null)"" || continue
  echo ""$r|$(git -C ""$r"" remote get-url origin 2>/dev/null)""
done
", string.Join(" ", list.Select(WinArg))).Output;
            var repos = output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("/")).Distinct()
                .Select(l => (Root: l.Split('|')[0], Remote: l.Contains("|") ? l.Substring(l.IndexOf('|') + 1) : "")).ToList();
            return repos.Where(r => !repos.Any(o => o.Root != r.Root && r.Root.StartsWith(o.Root.TrimEnd('/') + "/")))
                .GroupBy(r => r.Root).Select(g => g.First()).OrderBy(r => r.Root).ToList();
        }

        public static (string Root, string Remote) RepoOf(string distro, string folder)
        {
            var output = RunScript(distro, @"r=""$(git -C ""$1"" rev-parse --show-toplevel 2>/dev/null)"" || exit 1
echo ""$r|$(git -C ""$r"" remote get-url origin 2>/dev/null)""
", WinArg(folder)).Output.Trim();
            int bar = output.IndexOf('|');
            return bar < 0 ? (null, null) : (output.Substring(0, bar), output.Substring(bar + 1));
        }

        // A new note after the vault's project template; its sections stay empty for Claude to fill.
        public static string CreateNote(string vaultWindows, string name, string remote)
        {
            var folder = System.IO.Path.Combine(vaultWindows, "projects");
            Directory.CreateDirectory(folder);
            var file = System.IO.Path.Combine(folder, name + ".md");
            File.WriteAllText(file,
                "---\n" +
                "type: project\n" +
                $"title: {name}\n" +
                "tags: [repository]\n" +
                "status: active\n" +
                $"repo: {name}\n" +
                $"remote: {remote}\n" +
                "branches: []\n" +
                $"updated: {DateTime.Now:yyyy-MM-dd}\n" +
                "---\n\n## State\n\n## Tickets\n\n## Open questions\n\n## Links\n\n- [[decisions/index]]\n", Shell.Utf8);

            // The index keeps one line per note in alphabetical order.
            var index = System.IO.Path.Combine(folder, "index.md");
            if (File.Exists(index) && !File.ReadAllText(index).Contains($"[[{name}]]"))
            {
                var repo = Regex.Match(remote, "[:/]([^:/]+/[^/]+?)(\\.git)?$");
                var entry = $"- [[{name}]]: Repository {(repo.Success ? repo.Groups[1].Value : remote)}";
                var lines = File.ReadAllText(index).Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
                var entries = lines.Select((l, i) => (l, i)).Where(x => x.l.StartsWith("- [[")).ToList();
                var after = entries.LastOrDefault(x => string.Compare(x.l, entry, StringComparison.OrdinalIgnoreCase) < 0);
                int at = entries.Count == 0 ? lines.Count : after.l == null ? entries[0].i : after.i + 1;
                lines.Insert(at, entry);
                File.WriteAllText(index, string.Join("\n", lines) + "\n", Shell.Utf8);
            }
            Directory.CreateDirectory(System.IO.Path.Combine(vaultWindows, PersonalFolder, name));
            return file;
        }

        public const string GlobalSetsFile = "/.claude/global-agent-sets";

        // Sets that are global on this PC; the list lives outside the vault on purpose.
        public static List<string> GlobalSets(string distro)
        {
            var file = Wsl.UncPath(distro, Wsl.Home(distro) + GlobalSetsFile);
            if (!File.Exists(file)) return new List<string>();
            return File.ReadAllLines(file).Select(l => (l.Contains("#") ? l.Substring(0, l.IndexOf('#')) : l).Trim()).Where(l => l != "").ToList();
        }

        public static Task<(int Code, string Output)> SetGlobal(string distro, string set, bool global) => Task.Run(() => RunScript(distro, @"
f=""$HOME/.claude/global-agent-sets""; set=""$1""
touch ""$f""
grep -vxF ""$set"" ""$f"" > ""$f.tmp""; mv ""$f.tmp"" ""$f""
[ ""$2"" = 1 ] && echo ""$set"" >> ""$f""
[ -f ""$HOME/.claude/vault-link.sh"" ] && bash ""$HOME/.claude/vault-link.sh""
exit 0
", WinArg(set) + " " + (global ? "1" : "0")));

        // Links new or removed vault agents into ~/.claude/agents right away instead of at the next Claude start.
        public static Task<(int Code, string Output)> SyncAgents(string distro) =>
            Task.Run(() => RunScript(distro, "[ -f \"$HOME/.claude/vault-link.sh\" ] && bash \"$HOME/.claude/vault-link.sh\"; exit 0\n", ""));

        // Arguments to wsl.exe are split by Windows rules: double quotes, not shell quotes.
        internal static string WinArg(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

        static (int Code, string Output) RunScript(string distro, string body, string arguments)
        {
            Directory.CreateDirectory(Payload.WorkDir);
            var script = System.IO.Path.Combine(Payload.WorkDir, $"project-{Guid.NewGuid():N}.sh");
            Shell.WriteUnix(script, body);
            try
            {
                return Shell.Capture("wsl.exe", $"-d {distro} -e sh \"{Wsl.ToLinux(distro, script)}\" {arguments}", 60000);
            }
            finally
            {
                File.Delete(script);
            }
        }
    }
}
