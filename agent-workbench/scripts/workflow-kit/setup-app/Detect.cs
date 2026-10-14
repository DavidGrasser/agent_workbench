using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ClaudeWorkflowSetup
{
    public enum Presence { Unknown, Missing, Partial, Present }

    public sealed class Finding
    {
        public Presence State;
        public string Detail = "";

        public static Finding Of(bool present, string detail = "") =>
            new Finding { State = present ? Presence.Present : Presence.Missing, Detail = present ? detail : "" };
    }

    public sealed class Detection
    {
        public List<string> Distros = new List<string>();
        public string Distro;
        public string LinuxUser;
        public bool HasUser => !string.IsNullOrEmpty(LinuxUser) && LinuxUser != "root";
        public Dictionary<string, Finding> Components = new Dictionary<string, Finding>();
        public VaultInfo Vault;

        public string WslSummary =>
            Distros.Count == 0 ? "not set up yet, Ubuntu will be installed"
            : !HasUser ? $"{Distro} without a user, one will be created"
            : $"{Distro}, user {LinuxUser}";

        // One wsl.exe round trip for every Linux-side check; each line is key=detail.
        const string Probe = @"
id -un | sed 's/^/user=/'
[ ""$(id -u)"" = 0 ] && exit 0
c=""$HOME/.local/bin/claude""; [ -x ""$c"" ] || c=""$(command -v claude)""
[ -n ""$c"" ] && echo ""claude=$(""$c"" --version 2>/dev/null | head -1)""
[ -f ""$HOME/.claude/settings.json"" ] && [ -e ""$HOME/.claude/agents"" ] && [ -e ""$HOME/.claude/hooks"" ] && echo 'config=settings.json, agents, hooks'
s=""$HOME/.local/bin/starship""; [ -x ""$s"" ] || s=""$(command -v starship)""
if command -v tmux >/dev/null && [ -n ""$s"" ] && [ -f ""$HOME/.tmux.conf"" ] && [ -d ""$HOME/.tmux/plugins/tpm"" ]; then
  echo 'shell=tmux, Starship, plugins'
elif command -v tmux >/dev/null || [ -f ""$HOME/.tmux.conf"" ]; then
  echo 'shell~partly set up'
fi
[ -f ""$HOME/.claude.json"" ] && command -v python3 >/dev/null && python3 -c ""import json;s=json.load(open('$HOME/.claude.json')).get('mcpServers',{});print('mcp='+', '.join(s)) if s else None"" 2>/dev/null
x=""$HOME/.local/bin/codex""; [ -x ""$x"" ] || x=""$(command -v codex)""
[ -n ""$x"" ] && echo ""codex=$(""$x"" --version 2>/dev/null | head -1)""
x=""$HOME/.opencode/bin/opencode""; [ -x ""$x"" ] || x=""$(command -v opencode)""
[ -n ""$x"" ] && echo ""opencode=$(""$x"" --version 2>/dev/null | head -1)""
if [ -x /opt/google/chrome/chrome ] && { grep -qs '^\[mcp_servers.playwright\]' ""$HOME/.codex/config.toml"" || grep -qs '""playwright""' ""$HOME/.config/opencode/opencode.json""; }; then
  echo 'browser=Chrome and Playwright MCP'
elif [ -x /opt/google/chrome/chrome ]; then
  echo 'browser~Chrome without Playwright MCP'
fi
crontab -l 2>/dev/null | grep -q insights-monthly && echo 'cron=cron job registered'
n=""$(git config --global user.name 2>/dev/null)""; [ -n ""$n"" ] && echo ""git=$n <$(git config --global user.email)>""
exit 0
";

        public static Detection Run(string vaultPath)
        {
            var result = new Detection();
            DetectWsl(result);
            DetectWindows(result, vaultPath);
            foreach (var key in new[] { "claude", "codex", "opencode", "config", "shell", "mcp", "browser", "cron", "git" })
                if (!result.Components.ContainsKey(key)) result.Components[key] = Finding.Of(false);
            return result;
        }

        static void DetectWsl(Detection result)
        {
            result.Distros = Wsl.Distros();
            result.Distro = Wsl.Pick(result.Distros);
            if (result.Distro == null) return;

            Directory.CreateDirectory(Payload.WorkDir);
            var script = Path.Combine(Payload.WorkDir, "probe.sh");
            Shell.WriteUnix(script, Probe);
            try
            {
                var linux = Wsl.ToLinux(result.Distro, script);
                var (_, output) = Shell.Capture("wsl.exe", $"-d {result.Distro} -e sh \"{linux}\"", 30000);
                foreach (var line in output.Split('\n'))
                {
                    var m = Regex.Match(line.Trim(), "^([a-z]+)([=~])(.*)$");
                    if (!m.Success) continue;
                    if (m.Groups[1].Value == "user") { result.LinuxUser = m.Groups[3].Value; continue; }
                    result.Components[m.Groups[1].Value] = new Finding
                    {
                        State = m.Groups[2].Value == "=" ? Presence.Present : Presence.Partial,
                        Detail = m.Groups[3].Value,
                    };
                }
            }
            finally
            {
                File.Delete(script);
            }
        }

        static void DetectWindows(Detection result, string vaultPath)
        {
            var parts = new List<string>();
            var settings = Directory.GetDirectories(Path.Combine(Installer.LocalAppData, "Packages"), "Microsoft.WindowsTerminal*")
                .Select(d => Path.Combine(d, @"LocalState\settings.json")).FirstOrDefault(File.Exists);
            bool profile = (settings != null && Regex.IsMatch(File.ReadAllText(settings), "\"name\"\\s*:\\s*\"Claude Code\""))
                || File.Exists(Path.Combine(Installer.LocalAppData, @"Microsoft\Windows Terminal\Fragments\ClaudeWorkflow\claude.json"));
            if (profile) parts.Add("terminal profile");
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell\ClaudeCode"))
                if (key != null) parts.Add("context menu");
            var font = "MesloLGSDZNerdFont-Regular.ttf";
            if (File.Exists(Path.Combine(Installer.LocalAppData, @"Microsoft\Windows\Fonts", font))
                || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), font)))
                parts.Add("font");
            result.Components["terminal"] = new Finding
            {
                State = parts.Count == 3 ? Presence.Present : parts.Count > 0 ? Presence.Partial : Presence.Missing,
                Detail = parts.Count == 3 ? "profile, context menu, font" : parts.Count > 0 ? "only " + string.Join(", ", parts) : "",
            };

            result.Vault = VaultInfo.Scan(vaultPath);
            result.Components["vault"] = Finding.Of(result.Vault.LooksLikeVault, vaultPath);
            result.Components["obsidian"] = Finding.Of(File.Exists(Installer.ObsidianExe), "installed");

            var chromeData = Path.Combine(Installer.LocalAppData, @"Google\Chrome\User Data");
            bool extension = Directory.Exists(chromeData) && Directory.GetDirectories(chromeData)
                .Any(profileDir => Directory.Exists(Path.Combine(profileDir, "Extensions", Installer.ChromeExtensionId)));
            result.Components["chrome"] = Finding.Of(extension, "installed in Chrome");
        }
    }
}
