using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ClaudeWorkflowSetup
{
    public sealed class McpServer
    {
        public string Name = "";
        public string Type = "http";
        public string Url = "";
        public string Header = "Authorization";
        public string Scheme = "Bearer";
        public string Secret = "";
        public string Command = "";
        public string Args = "";
        public string Env = "";
        public bool NeedsSecret;

        public bool IsRemote => Type != "stdio";

        public string AuthValue => Secret == "" ? "" : (Scheme.Trim() == "" ? Secret : Scheme.Trim() + " " + Secret);

        public string Summary => IsRemote ? $"{Type.ToUpperInvariant()}  {Url}" : $"stdio  {Command} {Args}".Trim();

        public Dictionary<string, object> ToConfig()
        {
            var entry = new Dictionary<string, object> { ["type"] = Type };
            if (IsRemote)
            {
                entry["url"] = Url.Trim();
                if (Header.Trim() != "" && Secret != "") entry["headers"] = new Dictionary<string, object> { [Header.Trim()] = AuthValue };
            }
            else
            {
                entry["command"] = Command.Trim();
                entry["args"] = SplitArgs(Args);
                var env = Env.Split('\n').Select(l => l.Trim()).Where(l => l.Contains("="))
                    .ToDictionary(l => l.Substring(0, l.IndexOf('=')).Trim(), l => (object)l.Substring(l.IndexOf('=') + 1).Trim());
                if (env.Count > 0) entry["env"] = env;
            }
            return entry;
        }

        public static McpServer Parse(string name, Dictionary<string, object> entry)
        {
            string Str(string key) => entry.TryGetValue(key, out var v) ? v as string ?? "" : "";
            var server = new McpServer { Name = name, Type = Str("type") == "" ? (entry.ContainsKey("url") ? "http" : "stdio") : Str("type") };
            server.Url = Str("url");
            server.Command = Str("command");
            if (entry.TryGetValue("args", out var a) && a is System.Collections.IEnumerable args && !(a is string))
                server.Args = string.Join(" ", args.Cast<object>().Select(x => x.ToString().Contains(" ") ? $"\"{x}\"" : x.ToString()));
            if (entry.TryGetValue("env", out var e) && e is Dictionary<string, object> env)
                server.Env = string.Join("\n", env.Select(kv => $"{kv.Key}={((kv.Value as string) ?? "").Replace("{{SECRET}}", "")}"));
            if (entry.TryGetValue("headers", out var h) && h is Dictionary<string, object> headers && headers.Count > 0)
            {
                var auth = headers.FirstOrDefault(kv => (kv.Value as string ?? "").Contains("{{SECRET}}"));
                if (auth.Key == null) auth = headers.First();
                var value = auth.Value as string ?? "";
                server.Header = auth.Key;
                server.Scheme = value.Contains(" ") ? value.Substring(0, value.IndexOf(' ')) : "";
                server.NeedsSecret = value.Contains("{{SECRET}}");
                server.Secret = server.NeedsSecret ? "" : (value.Contains(" ") ? value.Substring(value.IndexOf(' ') + 1) : value);
            }
            return server;
        }

        static List<string> SplitArgs(string line) =>
            Regex.Matches(line ?? "", "\"([^\"]*)\"|(\\S+)").Cast<Match>()
                .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToList();
    }

    public static class Payload
    {
        public static readonly string WorkDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"AgentWorkbench\Setup");
        public static readonly string Dir = Path.Combine(WorkDir, "payload");

        static Stream Open() => Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");

        public static string ReadEntry(string name)
        {
            using (var zip = new ZipArchive(Open(), ZipArchiveMode.Read))
            {
                var entry = zip.GetEntry(name);
                if (entry == null) return null;
                using (var reader = new StreamReader(entry.Open(), Encoding.UTF8)) return reader.ReadToEnd();
            }
        }

        public static bool HasFolder(string prefix)
        {
            using (var zip = new ZipArchive(Open(), ZipArchiveMode.Read))
                return zip.Entries.Any(e => e.FullName.StartsWith(prefix + "/"));
        }

        public static void Extract()
        {
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
            Directory.CreateDirectory(Dir);
            using (var zip = new ZipArchive(Open(), ZipArchiveMode.Read)) zip.ExtractToDirectory(Dir);
        }

        public static Dictionary<string, string> Manifest()
        {
            var result = new Dictionary<string, string>();
            foreach (var line in (ReadEntry("manifest.env") ?? "").Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) result[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim().Trim('\'');
            }
            return result;
        }

        public static List<McpServer> McpServers()
        {
            var json = ReadEntry("mcp/servers.json");
            if (json == null) return new List<McpServer>();
            var servers = new JavaScriptSerializer().Deserialize<Dictionary<string, Dictionary<string, object>>>(json);
            return servers.Select(s => McpServer.Parse(s.Key, s.Value)).ToList();
        }
    }

    public static class Shell
    {
        public static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        static ProcessStartInfo Info(string file, string args) => new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            EnvironmentVariables = { ["WSL_UTF8"] = "1" },
        };

        // wsl.exe answers in UTF-16 on older builds even with WSL_UTF8 set.
        static string Clean(string s) => (s ?? "").Replace("\0", "").Replace("\r", "");

        public static (int Code, string Output) Capture(string file, string args, int timeoutMs = 60000)
        {
            try
            {
                using (var p = Process.Start(Info(file, args)))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    var output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs)) { p.Kill(); return (-1, "timeout"); }
                    return (p.ExitCode, Clean(output + err.Result).Trim());
                }
            }
            catch (Exception e)
            {
                return (-1, e.Message);
            }
        }

        public static Task<int> Stream(string file, string args, Action<string> onLine) => Task.Run(() =>
        {
            try
            {
                using (var p = new Process { StartInfo = Info(file, args) })
                {
                    DataReceivedEventHandler handler = (s, e) =>
                    {
                        if (e.Data == null) return;
                        // winget redraws its progress bar with carriage returns.
                        var line = Clean(e.Data.Split('\r').Last());
                        if (line.Trim().Length > 0) onLine(line);
                    };
                    p.OutputDataReceived += handler;
                    p.ErrorDataReceived += handler;
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (Exception e)
            {
                onLine("  [warn] " + e.Message);
                return -1;
            }
        });

        public static string ShQuote(string s) => "'" + (s ?? "").Replace("'", "'\\''") + "'";

        public static void WriteUnix(string path, string text) => File.WriteAllText(path, text.Replace("\r\n", "\n"), Utf8);
    }

    public static class Wsl
    {
        public static List<string> Distros()
        {
            var (code, output) = Shell.Capture("wsl.exe", "-l -q");
            if (code != 0) return new List<string>();
            return output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("docker-desktop")).ToList();
        }

        public static string Pick(List<string> distros) =>
            distros.FirstOrDefault(d => d.StartsWith("Ubuntu", StringComparison.OrdinalIgnoreCase)) ?? distros.FirstOrDefault();

        public static bool HasUser(string distro)
        {
            var (code, output) = Shell.Capture("wsl.exe", $"-d {distro} -e id -u");
            return code == 0 && output.Trim() != "0" && output.Trim().All(char.IsDigit);
        }

        // AGENT_WORKBENCH_HOME points the app at another Linux home, for testing against a copy.
        public static string Home(string distro) => Environment.GetEnvironmentVariable("AGENT_WORKBENCH_HOME")
            ?? Shell.Capture("wsl.exe", $"-d {distro} -e sh -c \"printf %s $HOME\"").Output;

        public static string ToLinux(string distro, string windowsPath) =>
            Shell.Capture("wsl.exe", $"-d {distro} -e wslpath -a \"{windowsPath.TrimEnd('\\')}\"").Output.Trim();

        // Windows-side path of a file inside the distro, readable without a wsl.exe round trip.
        public static string UncPath(string distro, string linuxPath) => $@"\\wsl.localhost\{distro}{linuxPath.Replace('/', '\\')}";

        public static Task<int> CreateUser(string distro, string user, string password, Action<string> log)
        {
            var script = Path.Combine(Payload.WorkDir, "create-user.sh");
            Directory.CreateDirectory(Payload.WorkDir);
            Shell.WriteUnix(script,
                "set -e\n" +
                $"U={Shell.ShQuote(user)}\n" +
                "id \"$U\" >/dev/null 2>&1 || useradd -m -s /bin/bash -G sudo \"$U\"\n" +
                $"echo \"$U:\"{Shell.ShQuote(password)} | chpasswd\n" +
                "printf '[user]\\ndefault=%s\\n' \"$U\" > /etc/wsl.conf.user\n" +
                "if grep -q '^\\[user\\]' /etc/wsl.conf 2>/dev/null; then sed -i \"s/^default=.*/default=$U/\" /etc/wsl.conf;" +
                " else cat /etc/wsl.conf.user >> /etc/wsl.conf; fi\n" +
                "rm -f /etc/wsl.conf.user\n");
            return Task.Run(async () =>
            {
                try
                {
                    var linuxScript = ToLinux(distro, script);
                    int code = await Shell.Stream("wsl.exe", $"-d {distro} -u root -e sh \"{linuxScript}\"", log);
                    Shell.Capture("wsl.exe", $"--terminate {distro}");
                    return code;
                }
                finally
                {
                    File.Delete(script);
                }
            });
        }

        public static List<McpServer> ConfiguredMcpServers(string distro)
        {
            try
            {
                var file = UncPath(distro, Home(distro) + "/.claude.json");
                if (!File.Exists(file)) return new List<McpServer>();
                var config = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                if (!(config.TryGetValue("mcpServers", out var s) && s is Dictionary<string, object> servers)) return new List<McpServer>();
                return servers.Where(kv => kv.Value is Dictionary<string, object>)
                    .Select(kv => McpServer.Parse(kv.Key, (Dictionary<string, object>)kv.Value)).ToList();
            }
            catch
            {
                return new List<McpServer>();
            }
        }
    }

    public sealed class AgentInfo
    {
        public string Name;
        public string Description;
        public string File;
    }

    // What a vault folder offers to Claude, following the vault layout convention in README.md.
    public sealed class VaultInfo
    {
        public string Path;
        public bool Exists;
        public bool IsObsidian;
        public List<AgentInfo> Agents = new List<AgentInfo>();
        public int Commands;
        public int Hooks;
        public int Memories;

        public bool LooksLikeVault => IsObsidian || Agents.Count > 0 || Memories > 0;

        public string Summary
        {
            get
            {
                if (!Exists) return "The folder does not exist yet.";
                var parts = new List<string>();
                if (IsObsidian) parts.Add("Obsidian vault");
                parts.Add(Plural(Agents.Count, "agent"));
                parts.Add(Plural(Commands, "command"));
                parts.Add(Plural(Hooks, "hook"));
                parts.Add(Memories == 1 ? "1 memory" : $"{Memories} memories");
                return "Found: " + string.Join(", ", parts);
            }
        }

        static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

        public static VaultInfo Scan(string path)
        {
            var info = new VaultInfo { Path = path };
            try
            {
                info.Exists = !string.IsNullOrWhiteSpace(path) && System.IO.Path.IsPathRooted(path) && Directory.Exists(path);
                if (!info.Exists) return info;
                string Sub(string folder) => System.IO.Path.Combine(path, folder);
                int Count(string folder, string pattern) => Directory.Exists(Sub(folder)) ? Directory.GetFiles(Sub(folder), pattern).Length : 0;
                info.IsObsidian = Directory.Exists(Sub(".obsidian"));
                info.Commands = Count("commands", "*.md");
                info.Hooks = Count(@"scripts\hooks", "*.py") + Count(@"scripts\hooks", "*.sh");
                info.Memories = Math.Max(0, Count("memory", "*.md") - (File.Exists(Sub(@"memory\MEMORY.md")) ? 1 : 0));
                if (Directory.Exists(Sub("agents")))
                    info.Agents = Directory.GetFiles(Sub("agents"), "*.md").Select(ReadAgent).OrderBy(a => a.Name).ToList();
            }
            catch (Exception)
            {
                info.Exists = false;
            }
            return info;
        }

        static AgentInfo ReadAgent(string file)
        {
            var agent = new AgentInfo { File = file, Name = System.IO.Path.GetFileNameWithoutExtension(file), Description = "" };
            var front = Regex.Match(File.ReadAllText(file).Replace("\r\n", "\n"), "^---\\s*\\n(.*?)\\n---", RegexOptions.Singleline);
            if (!front.Success) return agent;
            var name = Regex.Match(front.Groups[1].Value, "^name:\\s*(.+)$", RegexOptions.Multiline);
            var description = Regex.Match(front.Groups[1].Value, "^description:\\s*(.+)$", RegexOptions.Multiline);
            if (name.Success) agent.Name = name.Groups[1].Value.Trim().Trim('"');
            if (description.Success) agent.Description = description.Groups[1].Value.Trim().Trim('"');
            return agent;
        }
    }

    // Remembered by the installed Agent Workbench app.
    public sealed class AppSettings
    {
        public string VaultPath;
        public string Distro;
        public string InstalledAt;
        public List<string> ExtraProjects = new List<string>();
        // Where each vault project's repository lives on this PC; the path differs per machine,
        // so it is not stored in the vault note.
        public Dictionary<string, string> ProjectPaths = new Dictionary<string, string>();
        public bool Maximized;

        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentWorkbench");
        public static readonly string InstalledExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\AgentWorkbench\Agent-Workbench.exe");
        static string FilePath => Path.Combine(Dir, "settings.json");

        // Until 13.10.2026 the app was called Claude Configuration.
        public static readonly string LegacyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeConfiguration");
        public static readonly string LegacyExeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\ClaudeConfiguration");
        public const string LegacyShortcut = "Claude Configuration.lnk";

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this), Shell.Utf8);
        }

        public static AppSettings Load()
        {
            try
            {
                var path = File.Exists(FilePath) ? FilePath : Path.Combine(LegacyDir, "settings.json");
                return new JavaScriptSerializer().Deserialize<AppSettings>(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }
    }

    // Survives the reboot that a fresh WSL install needs. Encrypted for the current Windows user,
    // because it carries the MCP tokens.
    public sealed class SetupState
    {
        public List<string> Components = new List<string>();
        public string McpServersJson = "{}";
        public string VaultPath;
        public string VaultSource;
        public bool AddWorkflow;
        public bool UseChrome = true;
        public string Distro;
        public string GitName;
        public string GitEmail;

        static string FilePath => Path.Combine(Payload.WorkDir, "resume.bin");

        public bool Has(string component) => Components.Contains(component);

        public void Save()
        {
            Directory.CreateDirectory(Payload.WorkDir);
            var json = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(this));
            File.WriteAllBytes(FilePath, ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));
        }

        public static SetupState Load()
        {
            try
            {
                var json = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
                return new JavaScriptSerializer().Deserialize<SetupState>(Encoding.UTF8.GetString(json));
            }
            catch
            {
                return null;
            }
        }

        public static void Clear()
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
    }
}
