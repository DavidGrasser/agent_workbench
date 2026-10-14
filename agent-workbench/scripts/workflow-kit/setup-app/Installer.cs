using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace ClaudeWorkflowSetup
{
    public sealed class Installer
    {
        public const string ChromeExtensionId = "fcoeoabgfenejglbffodgkkbkcdhcgfn";
        public const string ChromeExtensionUrl = "https://chromewebstore.google.com/detail/" + ChromeExtensionId;
        static readonly string[] LinuxComponents = { "shell", "claude", "codex", "opencode", "vault", "config", "mcp", "browser", "cron", "git" };
        public static readonly (string Key, string Name, string Color)[] OtherTools = { ("codex", "Codex", "#A6E3A1"), ("opencode", "OpenCode", "#FAB387") };

        readonly SetupState state;
        readonly Action<string> log;
        readonly Action<string> stepStarted;
        int stepsDone;

        public int Warnings { get; private set; }
        public int TotalSteps { get; }
        public event Action<double> Progress;

        public static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public static string ObsidianExe => Path.Combine(LocalAppData, @"Programs\Obsidian\Obsidian.exe");

        public Installer(SetupState state, Action<string> log, Action<string> stepStarted)
        {
            this.state = state;
            this.log = log;
            this.stepStarted = stepStarted;
            TotalSteps = CountSteps();
        }

        // Mirrors the "==>" sections install.sh prints for the chosen components.
        int CountSteps()
        {
            int n = 2;
            if (LinuxComponents.Any(state.Has)) n += 3;
            if (state.Has("claude")) n++;
            foreach (var t in OtherTools) if (state.Has(t.Key)) n++;
            if (OtherTools.Any(t => state.Has(t.Key))) n++;
            if (state.Has("vault")) n++;
            if (state.Has("browser")) n++;
            if (state.AddWorkflow) n++;
            if (state.Has("shell")) n += 4;
            foreach (var c in new[] { "config", "mcp", "cron", "git" }) if (state.Has(c)) n++;
            if (state.Has("terminal")) n += 3;
            if (state.Has("obsidian")) n += 1;
            return n;
        }

        void Line(string line)
        {
            if (line.StartsWith("==> "))
            {
                stepsDone++;
                stepStarted(line.Substring(4));
                Progress?.Invoke(Math.Min(1.0, (double)stepsDone / TotalSteps));
            }
            if (line.Contains("[warn]") || line.Contains("[fail]")) Warnings++;
            log(line);
        }

        void Step(string name) => Line("==> " + name);
        void Ok(string text) => Line("  [ok]   " + text);
        void Skip(string text) => Line("  [skip] " + text);
        void Warn(string text) => Line("  [warn] " + text);

        public async Task<bool> RunAsync()
        {
            Step("Unpacking the package");
            await Task.Run(() => Payload.Extract());
            Ok(Payload.Dir);

            try
            {
                if (LinuxComponents.Any(state.Has) && !await RunLinuxPart()) return false;
                if (state.Has("terminal")) await Task.Run(() => { InstallFonts(); InstallContextMenu(); InstallTerminalProfile(); });
                if (state.Has("obsidian")) await InstallObsidian();
                await Task.Run(() => InstallApp());
                return true;
            }
            finally
            {
                try { Directory.Delete(Payload.Dir, true); } catch { }
            }
        }

        const string BrowserPackages = @"
log=/tmp/agent-workbench-browser.log
major=$(node -p 'process.versions.node.split(""."")[0]' 2>/dev/null || echo 0)
if [ ""$major"" -ge 20 ] && command -v npx >/dev/null; then echo ""  [skip] Node.js $(node -v) present""
else
  { apt-get remove -y -qq npm >/dev/null 2>&1; curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && apt-get install -y -qq nodejs; } >$log 2>&1 \
    && echo ""  [ok]   Node.js $(node -v)"" || { tail -15 $log; echo '  [warn] installing Node.js failed'; exit 1; }
fi
if [ -x /opt/google/chrome/chrome ]; then echo '  [skip] Google Chrome present'
else
  (cd /tmp && npx -y playwright@latest install --with-deps chrome) >$log 2>&1 \
    && echo '  [ok]   Google Chrome' || { tail -15 $log; echo '  [warn] installing Google Chrome failed'; exit 1; }
fi
";

        async Task<bool> RunLinuxPart()
        {
            var d = state.Distro;
            var bundle = Wsl.ToLinux(d, Payload.Dir);

            Step("System packages (as root)");
            // The browser option needs Node.js 20 or newer for Playwright (Ubuntu 24.04 ships 18, so it
            // comes from NodeSource) and Google Chrome with its libraries, which Playwright installs.
            Shell.WriteUnix(Path.Combine(Payload.Dir, "apt.sh"),
                "export DEBIAN_FRONTEND=noninteractive\n" +
                "missing=''\n" +
                "for p in tmux git curl python3 rsync perl; do command -v $p >/dev/null || missing=\"$missing $p\"; done\n" +
                "if [ -z \"$missing\" ]; then echo '  [skip] all packages present'\n" +
                "else apt-get update -qq && apt-get install -y -qq $missing >/dev/null && echo \"  [ok]  $missing\" || exit 1; fi\n" +
                (state.Has("browser") ? BrowserPackages : ""));
            if (await Shell.Stream("wsl.exe", $"-d {d} -u root -e sh \"{bundle}/apt.sh\"", Line) != 0)
            {
                Warn("installing the packages failed");
                return false;
            }

            var env = new List<string>
            {
                "COMPONENTS=" + Shell.ShQuote(string.Join(",", LinuxComponents.Where(state.Has))),
            };
            if (!string.IsNullOrEmpty(state.VaultPath))
                env.Add("VAULT_DIR=" + Shell.ShQuote(Wsl.ToLinux(d, state.VaultPath)));
            if (!string.IsNullOrEmpty(state.VaultSource)) env.Add("VAULT_SOURCE=" + Shell.ShQuote(state.VaultSource));
            if (state.AddWorkflow) env.Add("ADD_WORKFLOW=1");
            env.Add("CHROME=" + (state.UseChrome ? "1" : "0"));
            if (!string.IsNullOrEmpty(state.GitName)) env.Add("GIT_NAME_OVERRIDE=" + Shell.ShQuote(state.GitName));
            if (!string.IsNullOrEmpty(state.GitEmail)) env.Add("GIT_EMAIL_OVERRIDE=" + Shell.ShQuote(state.GitEmail));
            if (state.Has("mcp"))
            {
                File.WriteAllText(Path.Combine(Payload.Dir, "mcp-servers.json"), state.McpServersJson, Shell.Utf8);
                env.Add("MCP_SERVERS_FILE=" + Shell.ShQuote(bundle + "/mcp-servers.json"));
            }
            Shell.WriteUnix(Path.Combine(Payload.Dir, "run.sh"),
                string.Join("\n", env.Select(e => "export " + e)) + "\n" +
                $"exec bash {Shell.ShQuote(bundle + "/install.sh")}\n");

            int code = await Shell.Stream("wsl.exe", $"-d {d} -e bash \"{bundle}/run.sh\"", Line);
            if (code != 0) Warn($"install.sh ended with code {code}");
            return code == 0;
        }

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        static extern int AddFontResource(string file);

        void InstallFonts()
        {
            Step("Nerd Font (MesloLGSDZ)");
            var source = Path.Combine(Payload.Dir, "windows", "fonts");
            if (!Directory.Exists(source)) { Warn("no fonts in the package"); return; }
            var target = Path.Combine(LocalAppData, @"Microsoft\Windows\Fonts");
            Directory.CreateDirectory(target);
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Fonts"))
            {
                foreach (var file in Directory.GetFiles(source, "*.ttf"))
                {
                    var dest = Path.Combine(target, Path.GetFileName(file));
                    var style = Path.GetFileNameWithoutExtension(file).Split('-').Last();
                    var name = $"MesloLGSDZ Nerd Font {Regex.Replace(style, "([a-z])([A-Z])", "$1 $2")} (TrueType)";
                    if (File.Exists(dest)) { Skip(Path.GetFileName(file)); continue; }
                    File.Copy(file, dest);
                    key.SetValue(name, dest);
                    AddFontResource(dest);
                    Ok(Path.GetFileName(file));
                }
            }
        }

        void InstallContextMenu()
        {
            Step("Context menu");
            if (state.Has("claude")) InstallClaudeContextMenu();
            // Codex and OpenCode: their launcher takes the folder as argument and opens it in the
            // tool's own Windows Terminal profile.
            var home = Wsl.Home(state.Distro);
            foreach (var tool in OtherTools.Where(t => state.Has(t.Key)))
            {
                var command = $"wt.exe -p \"{tool.Name}\" wsl.exe -d {state.Distro} -e bash -lc \"{home}/{tool.Key}-tmux.sh '%V'\"";
                foreach (var path in new[] { $@"Software\Classes\Directory\shell\{tool.Name}Here", $@"Software\Classes\Directory\Background\shell\{tool.Name}Here" })
                {
                    using (var key = Registry.CurrentUser.CreateSubKey(path))
                    {
                        key.SetValue("", $"Open in {tool.Name}");
                        key.SetValue("Icon", AppSettings.InstalledExe + ",0");
                    }
                    using (var key = Registry.CurrentUser.CreateSubKey(path + @"\command")) key.SetValue("", command);
                }
                Ok($"\"Open in {tool.Name}\"");
            }
        }

        void InstallClaudeContextMenu()
        {
            var appDir = Path.Combine(AppData, "ClaudeCode");
            Directory.CreateDirectory(appDir);
            var ico = Path.Combine(appDir, "claude-code.ico");
            File.Copy(Path.Combine(Payload.Dir, "windows", "claude-code.ico"), ico, true);

            var launcher = File.ReadAllText(Path.Combine(Payload.Dir, "windows", "ClaudeHere.ps1"));
            launcher = Regex.Replace(launcher, "\\$distro\\s*=\\s*\"[^\"]*\"", m => $"$distro  = \"{state.Distro}\"");
            launcher = Regex.Replace(launcher, "\\$wslHome\\s*=\\s*\"[^\"]*\"", m => $"$wslHome = \"{Wsl.Home(state.Distro)}\"");
            var launcherPath = Path.Combine(appDir, "ClaudeHere.ps1");
            File.WriteAllText(launcherPath, launcher, new System.Text.UTF8Encoding(true));

            var command = $"powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{launcherPath}\" \"%V\"";
            foreach (var path in new[] { @"Software\Classes\Directory\shell\ClaudeCode", @"Software\Classes\Directory\Background\shell\ClaudeCode" })
            {
                using (var key = Registry.CurrentUser.CreateSubKey(path))
                {
                    key.SetValue("", "Open in Claude Code");
                    key.SetValue("Icon", ico);
                }
                using (var key = Registry.CurrentUser.CreateSubKey(path + @"\command")) key.SetValue("", command);
            }
            Ok("\"Open in Claude Code\"");
        }

        void InstallTerminalProfile()
        {
            Step("Windows Terminal profiles");
            var settings = Directory.GetDirectories(Path.Combine(LocalAppData, "Packages"), "Microsoft.WindowsTerminal*")
                .Select(dir => Path.Combine(dir, @"LocalState\settings.json")).FirstOrDefault(File.Exists);
            var existing = settings != null ? File.ReadAllText(settings) : "";
            bool Exists(string name) => Regex.IsMatch(existing, "\"name\"\\s*:\\s*\"" + Regex.Escape(name) + "\"");
            var source = Path.Combine(Payload.Dir, "windows", "terminal-fragment.json");
            if (!File.Exists(source)) { Warn("no profile in the package"); return; }

            var json = new JavaScriptSerializer();
            var fragment = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(source));
            var home = Wsl.Home(state.Distro);
            var template = ((System.Collections.ArrayList)fragment["profiles"]).OfType<Dictionary<string, object>>().FirstOrDefault();
            if (template == null) { Warn("no profile in the package"); return; }
            template.Remove("guid");
            template["icon"] = Path.Combine(AppData, @"ClaudeCode\claude-code.ico").Replace('\\', '/');

            // One profile per coding tool, all in the look of the Claude Code profile.
            var profiles = new System.Collections.ArrayList();
            var wanted = new List<(string Name, string Launcher, string Color)>();
            if (state.Has("claude")) wanted.Add(("Claude Code", "claude-tmux.sh", null));
            foreach (var tool in OtherTools)
                if (state.Has(tool.Key)) wanted.Add((tool.Name, $"{tool.Key}-tmux.sh", tool.Color));
            foreach (var (name, launcher, color) in wanted)
            {
                if (Exists(name)) { Skip($"{name}: profile already exists"); continue; }
                var profile = new Dictionary<string, object>(template)
                {
                    ["name"] = name,
                    ["commandline"] = $"wsl.exe -d {state.Distro} -e bash -lc {home}/{launcher}",
                };
                if (color != null) profile["tabColor"] = color;
                profiles.Add(profile);
                Ok(name);
            }
            fragment["profiles"] = profiles;
            var dir = Path.Combine(LocalAppData, @"Microsoft\Windows Terminal\Fragments\ClaudeWorkflow");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "claude.json"), json.Serialize(fragment), Shell.Utf8);
            if (Shell.Capture("where.exe", "wt.exe").Code != 0)
                Warn("Windows Terminal is missing, install it from the Microsoft Store");
        }

        async Task InstallObsidian()
        {
            Step("Obsidian");
            if (File.Exists(ObsidianExe))
                Skip("already installed");
            else if (Shell.Capture("where.exe", "winget.exe").Code == 0)
            {
                int code = await Shell.Stream("winget.exe",
                    "install -e --id Obsidian.Obsidian --silent --accept-package-agreements --accept-source-agreements", Line);
                if (code == 0 && File.Exists(ObsidianExe)) Ok("installed"); else Warn($"winget returned code {code}");
            }
            else
                Warn("winget is missing, install Obsidian from https://obsidian.md");

            // obsidian-git runs on the Windows side and needs Git for Windows.
            if (Shell.Capture("where.exe", "git.exe").Code == 0)
                Skip("Git for Windows present");
            else if (Shell.Capture("where.exe", "winget.exe").Code == 0)
            {
                int code = await Shell.Stream("winget.exe",
                    "install -e --id Git.Git --silent --accept-package-agreements --accept-source-agreements", Line);
                if (code == 0) Ok("Git for Windows installed, used by the obsidian-git plugin"); else Warn($"Git for Windows: winget returned code {code}");
            }
            else
                Warn("Git for Windows is missing, the obsidian-git plugin needs it");

            if (!string.IsNullOrEmpty(state.VaultPath)) RegisterVault(state.VaultPath);
        }

        void RegisterVault(string vaultPath)
        {
            var file = Path.Combine(AppData, @"obsidian\obsidian.json");
            var json = new JavaScriptSerializer();
            var config = File.Exists(file)
                ? json.Deserialize<Dictionary<string, object>>(File.ReadAllText(file))
                : new Dictionary<string, object>();
            var vaults = config.TryGetValue("vaults", out var v) && v is Dictionary<string, object> existing
                ? existing : new Dictionary<string, object>();
            bool known = vaults.Values.OfType<Dictionary<string, object>>()
                .Any(x => string.Equals(x["path"] as string, vaultPath, StringComparison.OrdinalIgnoreCase));
            if (known) { Skip("vault already known to Obsidian"); return; }
            foreach (var other in vaults.Values.OfType<Dictionary<string, object>>()) other.Remove("open");
            vaults[Guid.NewGuid().ToString("N").Substring(0, 16)] = new Dictionary<string, object>
            {
                ["path"] = vaultPath,
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["open"] = true,
            };
            config["vaults"] = vaults;
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, json.Serialize(config), Shell.Utf8);
            Ok("vault registered in Obsidian");
        }

        // Keeps a copy of this program as "Agent Workbench", so the setup can be reopened
        // later to manage the configuration.
        void InstallApp()
        {
            Step("Agent Workbench app");
            var self = Process.GetCurrentProcess().MainModule.FileName;
            var target = AppSettings.InstalledExe;
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (!string.Equals(self, target, StringComparison.OrdinalIgnoreCase)) File.Copy(self, target, true);

            var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Agent Workbench.lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            var link = shell.CreateShortcut(shortcut);
            link.TargetPath = target;
            link.IconLocation = target + ",0";
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.Description = "Manage your coding tools, agents and MCP servers";
            link.Save();
            Marshal.FinalReleaseComObject(shell);
            Ok("start menu entry \"Agent Workbench\"");

            // Keeps what the app remembered, such as the project paths, also from its old name.
            var settings = AppSettings.Load() ?? new AppSettings();
            // A run without the vault option has no vault path; it must not erase the known one.
            if (!string.IsNullOrEmpty(state.VaultPath)) settings.VaultPath = state.VaultPath;
            if (!string.IsNullOrEmpty(state.Distro)) settings.Distro = state.Distro;
            settings.InstalledAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            settings.Save();

            var legacyShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppSettings.LegacyShortcut);
            if (File.Exists(legacyShortcut) || Directory.Exists(AppSettings.LegacyExeDir) || Directory.Exists(AppSettings.LegacyDir))
            {
                try
                {
                    if (File.Exists(legacyShortcut)) File.Delete(legacyShortcut);
                    if (Directory.Exists(AppSettings.LegacyExeDir)) Directory.Delete(AppSettings.LegacyExeDir, true);
                    if (Directory.Exists(AppSettings.LegacyDir)) Directory.Delete(AppSettings.LegacyDir, true);
                    Ok("old \"Claude Configuration\" removed, its settings taken over");
                }
                catch (Exception ex) { Warn("old \"Claude Configuration\" could not be removed: " + ex.Message); }
            }
        }

        public static void OpenChromeExtension()
        {
            var chrome = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(LocalAppData, @"Google\Chrome\Application\chrome.exe"),
            }.FirstOrDefault(File.Exists);
            if (chrome != null) Process.Start(chrome, ChromeExtensionUrl);
            else Process.Start(new ProcessStartInfo(ChromeExtensionUrl) { UseShellExecute = true });
        }

        public static void StartClaude(string distro)
        {
            if (Shell.Capture("where.exe", "wt.exe").Code == 0)
                Process.Start(new ProcessStartInfo("wt.exe", "-p \"Claude Code\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("wsl.exe", $"-d {distro} -e bash -lc \"$HOME/claude-tmux.sh\"") { UseShellExecute = true });
        }

        public static void StartTool(string distro, string tool, string profile)
        {
            if (Shell.Capture("where.exe", "wt.exe").Code == 0)
                Process.Start(new ProcessStartInfo("wt.exe", $"-p \"{profile}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("wsl.exe", $"-d {distro} -e bash -lc \"$HOME/{tool}-tmux.sh\"") { UseShellExecute = true });
        }

        public static void OpenObsidian(string vaultPath)
        {
            var uri = "obsidian://open?path=" + Uri.EscapeDataString(vaultPath);
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
    }
}
