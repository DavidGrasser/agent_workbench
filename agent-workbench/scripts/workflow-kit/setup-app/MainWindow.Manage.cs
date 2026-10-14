using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ClaudeWorkflowSetup
{
    // The installed "Agent Workbench" app: overview, MCP servers, agents, projects and settings.
    public partial class MainWindow
    {
        AppSettings appSettings;
        readonly Dictionary<Page, RadioButton> nav = new Dictionary<Page, RadioButton>();
        AgentFile editedAgent;
        AgentForm agentForm;

        void BuildNav()
        {
            NavList.Children.Clear();
            foreach (var (target, label) in new[] { (Page.Overview, "Overview"), (Page.McpManage, "MCP servers"), (Page.Agents, "Agents"), (Page.Projects, "Projects"), (Page.Config, "Settings") })
            {
                var item = new RadioButton { Style = (Style)FindResource("Nav"), Content = label, GroupName = "nav" };
                item.Checked += async (s, e) =>
                {
                    if (reverting || page == target) return;
                    if (!await ConfirmLeave())
                    {
                        reverting = true;
                        MarkNav(NavTarget(page));
                        reverting = false;
                        return;
                    }
                    if (target == Page.Overview) await ShowOverview(appSettings);
                    else if (target == Page.McpManage) await ShowMcpManage();
                    else if (target == Page.Projects) ShowProjects();
                    else if (target == Page.Config) ShowConfig(null);
                    else ShowAgents();
                };
                nav[target] = item;
                NavList.Children.Add(item);
            }
        }

        bool reverting;

        static Page NavTarget(Page p) => p == Page.AgentEdit ? Page.Agents : p == Page.Project ? Page.Projects : p;

        async void Go(Action action)
        {
            if (await ConfirmLeave()) action();
        }

        void MarkNav(Page target)
        {
            if (nav.TryGetValue(target, out var item) && item.IsChecked != true) item.IsChecked = true;
        }

        // ------------------------------------------------------------ MCP servers

        static readonly (string Key, string Name, string Lead)[] McpTools =
        {
            ("claude", "Claude Code", "Servers Claude can use in every project. Changes are written through Claude's own CLI and apply to new Claude sessions."),
            ("codex", "Codex", "Servers Codex can use in every project, kept in ~/.codex/config.toml. A backup is written before saving; changes apply to new Codex sessions."),
            ("opencode", "OpenCode", "Servers OpenCode can use in every project, kept in ~/.config/opencode/opencode.json. A backup is written before saving; changes apply to new OpenCode sessions."),
        };
        string mcpTool = "claude";

        static string McpToolName(string key) => McpTools.First(t => t.Key == key).Name;

        // Codex and OpenCode are optional: a tool counts once its binary or its config folder exists.
        HashSet<string> installedTools;

        async Task<HashSet<string>> InstalledTools()
        {
            if (installedTools != null) return installedTools;
            var output = await Task.Run(() =>
            {
                var probe = $"H={Shell.ShQuote(Wsl.Home(appSettings.Distro))}; " +
                            "for t in codex opencode; do if command -v $t >/dev/null || [ -x \"$H/.local/bin/$t\" ] || [ -x \"$H/.$t/bin/$t\" ]; then echo $t; fi; done; " +
                            "[ -d \"$H/.codex\" ] && echo codex; [ -d \"$H/.config/opencode\" ] && echo opencode; true";
                return Shell.Capture("wsl.exe", $"-d {appSettings.Distro} -e bash -lc {Projects.WinArg(probe)}").Output;
            });
            installedTools = new HashSet<string>(output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())) { "claude" };
            return installedTools;
        }

        async Task ShowMcpManage() => await ShowMcpManage(mcpTool);

        async Task ShowMcpManage(string tool)
        {
            mcpTool = tool;
            ShowPage(Page.McpManage);
            MarkNav(Page.McpManage);
            McpLead.Text = McpTools.First(t => t.Key == tool).Lead;
            McpScope.Children.Clear();
            var installed = await InstalledTools();
            McpScope.Visibility = installed.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var t in McpTools.Where(t => installed.Contains(t.Key) || t.Key == tool))
            {
                var key = t.Key;
                var chip = new RadioButton { Style = StyleOf("Chip"), Content = t.Name, GroupName = "mcptool", IsChecked = key == tool };
                chip.Checked += async (s, e) =>
                {
                    if (reverting) return;
                    if (await ConfirmLeave()) { await ShowMcpManage(key); return; }
                    reverting = true;
                    foreach (var other in McpScope.Children.OfType<RadioButton>())
                        if ((string)other.Tag == mcpTool) other.IsChecked = true;
                    reverting = false;
                };
                chip.Tag = key;
                McpScope.Children.Add(chip);
            }
            ManageMcpList.Children.Clear();
            mcpEdits.Clear();
            ManageMcpList.Children.Add(new TextBlock { Text = "Loading …", Foreground = Brush("Subtext") });
            List<McpServer> configured;
            try { configured = await Task.Run(() => ListMcp(tool)); }
            catch (Exception ex)
            {
                ManageMcpList.Children.Clear();
                ManageMcpList.Children.Add(new TextBlock { Text = "Reading the configuration failed: " + ex.Message, TextWrapping = TextWrapping.Wrap, Foreground = Brush("Red") });
                return;
            }
            if (tool != mcpTool) return;
            ManageMcpList.Children.Clear();
            if (configured.Count == 0)
                ManageMcpList.Children.Add(new TextBlock { Text = "No MCP servers configured yet.", FontSize = 12, Foreground = Brush("Subtext"), Margin = new Thickness(0, 0, 0, 10) });
            foreach (var server in configured) AddManagedMcp(server);

            if (tool != "claude") return;
            var agentOnly = await Task.Run(AgentOnlyServers);
            if (agentOnly.Count == 0) return;
            ManageMcpList.Children.Add(new TextBlock { Text = "ONLY FOR ONE AGENT", Style = StyleOf("GroupLabel") });
            var tiles = new WrapPanel();
            foreach (var (name, agent) in agentOnly)
            {
                var summary = agent.McpServers[name];
                var url = Regex.Match(summary, "\"url\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
                var command = Regex.Match(summary, "\"command\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
                var target = agent;
                tiles.Children.Add(Tile(name, $"Agent {agent.Name}", url != "" ? url : command, () => EditAgent(target), 400));
            }
            ManageMcpList.Children.Add(tiles);
        }

        List<McpServer> ListMcp(string tool) =>
            tool == "claude" ? Wsl.ConfiguredMcpServers(appSettings.Distro) : ToolMcp.List(appSettings.Distro, tool);

        void ManageAddMcp_Click(object sender, RoutedEventArgs e) => AddManagedMcp(new McpServer());

        McpEditor AddManagedMcp(McpServer server)
        {
            var tool = mcpTool;
            var copyTargets = McpTools.Where(t => t.Key != tool && installedTools != null && installedTools.Contains(t.Key)).ToList();
            McpEditor editor = null;
            editor = new McpEditor(this, server, async () =>
            {
                if (editor.OriginalName == "") { ManageMcpList.Children.Remove(editor.Root); mcpEdits.Remove(editor); return; }
                if (MessageBox.Show(this, $"Remove the MCP server {editor.OriginalName} from {McpToolName(tool)}?", "Agent Workbench",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                var (code, output) = await RemoveMcp(tool, editor.OriginalName);
                if (code == 0) { ManageMcpList.Children.Remove(editor.Root); mcpEdits.Remove(editor); }
                else ShowStatus(editor, false, "Removing failed: " + output);
            });

            var save = new Button { Content = server.Name == "" ? "Add" : "Save", Style = (Style)FindResource("Primary"),
                Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 0, 10) };
            // A new Claude Code server can live inside its own agent instead of the user scope.
            CheckBox dedicated = null;
            if (server.Name == "" && tool == "claude")
            {
                dedicated = new CheckBox { Style = StyleOf("ChipToggle"), Content = "Only for its own agent", IsChecked = true, Margin = new Thickness(0, 0, 10, 0) };
                editor.Footer.Children.Add(dedicated);
                editor.Footer.Children.Add(new TextBlock { FontSize = 12, Foreground = Brush("Subtext"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 640, Text = "Creates the agent mcp-<name> with this server inside. Only that agent gets the tools; the main session and the other agents never see them. " +
                                           "The token goes to ~/.config/agent-workbench/secrets.env, not into the vault." });
                editor.Footer.Visibility = Visibility.Visible;
            }
            async Task<bool> Save()
            {
                var error = editor.Validate();
                if (error != null) { ShowStatus(editor, false, error); return false; }
                var updated = editor.Read();
                if (dedicated?.IsChecked == true)
                {
                    save.IsEnabled = false;
                    ShowStatus(editor, null, "Creating the agent …");
                    var (ok, message) = await CreateMcpAgent(updated);
                    save.IsEnabled = true;
                    if (!ok) { ShowStatus(editor, false, message); return false; }
                    mcpEdits.Remove(editor);
                    await ShowMcpManage("claude");
                    Notify(message);
                    return true;
                }
                save.IsEnabled = false;
                ShowStatus(editor, null, "Saving …");
                var (code, output) = await SaveMcp(tool, editor.OriginalName, updated);
                save.IsEnabled = true;
                if (code != 0) { ShowStatus(editor, false, "Saving failed: " + output); return false; }
                mcpEdits.Remove(editor);
                var index = ManageMcpList.Children.IndexOf(editor.Root);
                if (index >= 0)
                {
                    ManageMcpList.Children.RemoveAt(index);
                    ShowStatus(AddManagedMcp(updated, index), true, $"\u2713 Saved. New {McpToolName(tool)} sessions use it.");
                }
                return true;
            }
            save.Click += async (s, e) => await Save();
            mcpEdits[editor] = (Snapshot(editor.Read()), Save);
            editor.Actions.Children.Insert(0, save);

            if (server.Name != "")
                foreach (var other in copyTargets)
                {
                    var target = other;
                    var copy = new Button { Content = "Copy to " + target.Name, Style = (Style)FindResource("Secondary"),
                        Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
                    copy.Click += async (s, e) =>
                    {
                        copy.IsEnabled = false;
                        ShowStatus(editor, null, $"Copying to {target.Name} …");
                        var (code, output) = await SaveMcp(target.Key, server.Name, server, replace: true);
                        copy.IsEnabled = true;
                        ShowStatus(editor, code == 0, code == 0 ? $"✓ {target.Name} has it now; an entry of the same name was replaced." : "Copying failed: " + output);
                    };
                    editor.Footer.Children.Add(copy);
                    editor.Footer.Visibility = Visibility.Visible;
                }

            ManageMcpList.Children.Add(editor.Root);
            return editor;
        }

        static (int, string) RunWithInput(string file, string args, string input)
        {
            var info = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            using (var process = Process.Start(info))
            {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit(30000);
                return (process.ExitCode, output.Trim());
            }
        }

        static string AgentNameFor(string server) => "mcp-" + Regex.Replace(server.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

        // The server goes into a new agent's frontmatter; secrets become ${VAR} references whose values
        // are kept in the env block of ~/.claude/settings.json, outside the vault.
        async Task<(bool, string)> CreateMcpAgent(McpServer server)
        {
            var agentName = AgentNameFor(server.Name);
            if (File.Exists(Path.Combine(AgentsFolder, agentName + ".md")) || Projects.Sets(appSettings.VaultPath).Values.Any(set => set.Contains(agentName)))
                return (false, $"An agent named {agentName} already exists.");
            var prefix = "AW_MCP_" + Regex.Replace(server.Name.ToUpperInvariant(), "[^A-Z0-9]+", "_");
            var secrets = new Dictionary<string, object>();
            var config = server.ToConfig();
            if (server.IsRemote && server.Secret != "")
            {
                secrets[prefix + "_TOKEN"] = server.Secret;
                var scheme = server.Scheme.Trim();
                config["headers"] = new Dictionary<string, object> { [server.Header.Trim()] = (scheme == "" ? "" : scheme + " ") + "${" + prefix + "_TOKEN}" };
            }
            if (config.TryGetValue("env", out var envObject) && envObject is Dictionary<string, object> env)
                foreach (var key in env.Keys.ToList())
                {
                    var name = prefix + "_" + Regex.Replace(key.ToUpperInvariant(), "[^A-Z0-9]+", "_");
                    secrets[name] = env[key];
                    env[key] = "${" + name + "}";
                }

            // Kept in ~/.config/agent-workbench/secrets.env, which every shell loads, so Claude Code,
            // Codex and OpenCode resolve the same variables. The value travels on stdin only.
            var store = Wsl.ToLinux(appSettings.Distro, appSettings.VaultPath) + "/scripts/secrets.py";
            foreach (var kv in secrets)
            {
                var (code, output) = await Task.Run(() => RunWithInput("wsl.exe",
                    $"-d {appSettings.Distro} -e python3 \"{store}\" set {kv.Key}", kv.Value?.ToString() ?? ""));
                if (code != 0) return (false, "Saving the token failed: " + output);
            }

            Directory.CreateDirectory(AgentsFolder);
            AgentFile.Create(AgentsFolder, agentName,
                $"Works with the {server.Name} MCP server, which only this agent can use. Use it for every task that needs {server.Name}.",
                "sonnet", new List<string> { "mcp__" + server.Name },
                $"You are the only agent with access to the {server.Name} MCP server. Use its tools to carry out the task you are given, " +
                "then report what you found or did. Change data only when the task asks for it.",
                new Dictionary<string, string> { [server.Name] = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(config) });
            await Projects.SyncAgents(appSettings.Distro);
            return (true, $"\u2713 Agent {agentName} created; only it can use {server.Name}. New Claude sessions have it.");
        }

        // Servers that live inside an agent, from every agent file of the vault.
        List<(string Server, AgentFile Agent)> AgentOnlyServers()
        {
            var folders = new List<string> { AgentsFolder };
            var sets = Path.Combine(appSettings.VaultPath ?? "", Projects.PersonalFolder);
            if (Directory.Exists(sets)) folders.AddRange(Directory.GetDirectories(sets));
            var result = new List<(string, AgentFile)>();
            foreach (var folder in folders.Where(Directory.Exists))
                foreach (var file in Directory.GetFiles(folder, "*.md").OrderBy(f => f))
                {
                    var agent = AgentFile.Load(file);
                    foreach (var name in agent.McpServers.Keys) result.Add((name, agent));
                }
            return result;
        }

        McpEditor AddManagedMcp(McpServer server, int index)
        {
            var editor = AddManagedMcp(server);
            ManageMcpList.Children.Remove(editor.Root);
            ManageMcpList.Children.Insert(index, editor.Root);
            return editor;
        }

        Task<(int Code, string Output)> RemoveMcp(string tool, string name) =>
            tool == "claude" ? ClaudeCli.RemoveMcp(appSettings.Distro, name)
                             : ToolMcp.Save(appSettings.Distro, tool, new McpServer[0], new[] { name });

        // Claude's CLI cannot update a server in place: the old entry is removed first. With replace, an
        // entry of the same name is dropped silently when present.
        async Task<(int, string)> SaveMcp(string tool, string originalName, McpServer server, bool replace = false)
        {
            if (tool != "claude")
                return await ToolMcp.Save(appSettings.Distro, tool, new[] { server }, originalName != "" && originalName != server.Name ? new[] { originalName } : new string[0]);
            if (replace)
            {
                if (Wsl.ConfiguredMcpServers(appSettings.Distro).Any(c => c.Name == server.Name))
                {
                    var removed = await ClaudeCli.RemoveMcp(appSettings.Distro, server.Name);
                    if (removed.Code != 0) return removed;
                }
            }
            else if (originalName != "")
            {
                var removed = await ClaudeCli.RemoveMcp(appSettings.Distro, originalName);
                if (removed.Code != 0) return removed;
            }
            return await ClaudeCli.AddMcp(appSettings.Distro, server);
        }

        void ShowStatus(McpEditor editor, bool? ok, string text)
        {
            editor.Status.Visibility = Visibility.Visible;
            editor.Status.Foreground = ok == null ? Brush("Subtext") : ok.Value ? Brush("Green") : Brush("Red");
            editor.Status.Text = text;
        }

        // ------------------------------------------------------------------ agents

        string AgentsFolder => Path.Combine(appSettings.VaultPath ?? "", "agents");

        async void ShowAgents()
        {
            ShowPage(Page.Agents);
            MarkNav(Page.Agents);
            AgentList.Children.Clear();
            AgentsLead.Text = "Agents in the vault: agents/ is available everywhere, a set in project-agents/ in the projects it is linked to, " +
                              "or everywhere when this PC lists it. Changes apply to new sessions; Codex and OpenCode, when installed, get converted copies.";
            var globalSets = await Task.Run(() => Projects.GlobalSets(appSettings.Distro));
            var groups = new List<(string Label, string Folder)> { ("GLOBAL \u00b7 agents/", AgentsFolder) };
            foreach (var set in Projects.Sets(appSettings.VaultPath).Keys)
                groups.Add(($"SET {set} \u00b7 {(globalSets.Contains(set) ? "global on this PC" : "linked per project")}",
                    Path.Combine(appSettings.VaultPath, Projects.PersonalFolder, set)));
            agentCards.Clear();
            foreach (var (label, folder) in groups)
            {
                var heading = new TextBlock { Text = label, Style = (Style)FindResource("GroupLabel") };
                AgentList.Children.Add(heading);
                var files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.md").OrderBy(f => f).ToList() : new List<string>();
                if (files.Count == 0)
                    AgentList.Children.Add(new TextBlock { Text = "No agents.", FontSize = 12, Foreground = Brush("Subtext"), Margin = new Thickness(0, 0, 0, 8) });
                foreach (var file in files)
                {
                    var agent = AgentFile.Load(file);
                    var tools = agent.Tools == null ? "all tools" : $"{agent.Tools.Count} tool{(agent.Tools.Count == 1 ? "" : "s")}";
                    var model = agent.Model == "" ? "model inherited" : agent.Model;
                    var card = Tile(agent.Name, $"{model} \u00b7 {tools}", Shorten(agent.Description), () => EditAgent(agent));
                    agentCards.Add((card, agent, heading));
                    AgentList.Children.Add(card);
                }
            }
            AgentSearch_TextChanged(null, null);
        }

        readonly List<(Button Card, AgentFile Agent, TextBlock Heading)> agentCards = new List<(Button, AgentFile, TextBlock)>();

        void AgentSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            var query = AgentSearch.Text.Trim();
            AgentSearchHint.Visibility = AgentSearch.Text == "" ? Visibility.Visible : Visibility.Collapsed;
            foreach (var (card, agent, _) in agentCards)
                card.Visibility = query == "" || agent.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                                  || agent.Description.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var heading in agentCards.Select(c => c.Heading).Distinct())
                heading.Visibility = query == "" || agentCards.Any(c => c.Heading == heading && c.Card.Visibility == Visibility.Visible)
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        void NewAgent_Click(object sender, RoutedEventArgs e) => EditAgent(null);

        void EditAgent(AgentFile agent)
        {
            editedAgent = agent;
            ShowPage(Page.AgentEdit);
            MarkNav(Page.Agents);
            AgentEditTitle.Text = agent == null ? "New agent" : agent.Name;
            Crumb(AgentEditCrumb, ("Agents", () => Go(ShowAgents)), (AgentEditTitle.Text, null));
            AgentEditLead.Text = agent == null
                ? "Creates a new agent file in the vault."
                : "Only description, model and tools are changed in the file. Everything else stays as it is.";
            agentForm = new AgentForm(this, agent, appSettings.Distro);
            agentSnapshot = Snapshot(agentForm);
            AgentEditPanel.Children.Clear();
            AgentEditPanel.Children.Add(agentForm.Root);
            AgentEditScroll.ScrollToTop();
        }

        async Task<bool> SaveAgent()
        {
            var error = agentForm.Validate(AgentsFolder);
            if (error != null) { FooterNote.Text = error; return false; }
            try
            {
                if (editedAgent == null)
                {
                    Directory.CreateDirectory(AgentsFolder);
                    AgentFile.Create(AgentsFolder, agentForm.AgentName, agentForm.Description, agentForm.Model, agentForm.Tools, agentForm.Instructions);
                    await Projects.SyncAgents(appSettings.Distro);
                }
                else
                    editedAgent.Save(agentForm.Description, agentForm.Model, agentForm.Tools);
                agentSnapshot = null;
                Notify($"\u2713 Saved {agentForm.AgentName}.");
                if (page == Page.AgentEdit) ShowAgents();
                return true;
            }
            catch (Exception ex)
            {
                FooterNote.Text = "Saving failed: " + ex.Message;
                return false;
            }
        }

        internal Style StyleOf(string key) => (Style)FindResource(key);
        internal System.Windows.Media.Brush BrushOf(string key) => Brush(key);
    }

    sealed class AgentForm
    {
        public readonly StackPanel Root = new StackPanel();
        readonly AgentFile agent;
        readonly TextBox name, description, otherTools, instructions, pinnedModel;
        readonly Dictionary<string, RadioButton> models = new Dictionary<string, RadioButton>();
        readonly RadioButton allTools, selectedTools;
        readonly List<CheckBox> toolChips = new List<CheckBox>();
        readonly StackPanel toolsPanel = new StackPanel();
        readonly List<string> original;

        public AgentForm(MainWindow window, AgentFile agent, string distro)
        {
            this.agent = agent;
            original = agent?.Tools ?? new List<string>();
            void Label(string text) => Root.Children.Add(new TextBlock { Text = text, Style = window.StyleOf("GroupLabel") });
            TextBlock Hint(string text) => new TextBlock { Text = text, FontSize = 12, Foreground = window.BrushOf("Subtext"), Margin = new Thickness(0, 0, 0, 8) };

            Label("NAME");
            name = new TextBox { Text = agent?.Name ?? "", IsEnabled = agent == null };
            Root.Children.Add(name);
            if (agent == null) Root.Children.Add(Hint("Lowercase letters, digits and dashes. It becomes the file name and the agent's name."));

            Label("DESCRIPTION");
            Root.Children.Add(Hint("Claude reads this to decide when to use the agent. Say what it does and when to use it."));
            description = new TextBox { Text = agent?.Description ?? "", TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, Height = 72,
                IsEnabled = agent == null || agent.DescriptionEditable, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Root.Children.Add(description);
            if (agent != null && !agent.DescriptionEditable)
                Root.Children.Add(Hint("This description spans several lines in the file; edit it there."));

            Label("MODEL");
            Root.Children.Add(Hint("\"Latest\" follows the newest version of that family automatically. A fixed version stays until you change it."));
            var modelChips = new WrapPanel();
            var current = agent?.Model ?? "inherit";
            var options = new List<(string Key, string Text)>
            {
                ("inherit", "Same as main session"), ("opus", "Latest Opus"), ("sonnet", "Latest Sonnet"), ("haiku", "Latest Haiku"),
                ("fable", "Latest Fable"), ("pinned", "Fixed version"),
            };
            if (current == "") options.Insert(0, ("", "Not set"));
            bool pinned = options.All(o => o.Key != current);
            foreach (var (key, text) in options)
            {
                var chip = new RadioButton { Style = window.StyleOf("Chip"), Content = text, GroupName = "model", IsChecked = key == (pinned ? "pinned" : current) };
                models[key] = chip;
                modelChips.Children.Add(chip);
            }
            Root.Children.Add(modelChips);
            pinnedModel = new TextBox { Text = pinned ? current : "", Margin = new Thickness(0, 4, 0, 0), IsEnabled = pinned };
            models["pinned"].Checked += (s, e) => { pinnedModel.IsEnabled = true; pinnedModel.Focus(); };
            models["pinned"].Unchecked += (s, e) => pinnedModel.IsEnabled = false;
            Root.Children.Add(pinnedModel);
            Root.Children.Add(Hint("Fixed version: a full model ID, for example claude-opus-5-5 or claude-sonnet-5."));

            Label("TOOLS");
            var scope = new WrapPanel();
            allTools = new RadioButton { Style = window.StyleOf("Chip"), Content = "All tools", GroupName = "toolscope", IsChecked = agent?.Tools == null };
            selectedTools = new RadioButton { Style = window.StyleOf("Chip"), Content = "Only the selected tools", GroupName = "toolscope", IsChecked = agent?.Tools != null };
            allTools.Checked += (s, e) => toolsPanel.IsEnabled = false;
            selectedTools.Checked += (s, e) => toolsPanel.IsEnabled = true;
            scope.Children.Add(allTools);
            scope.Children.Add(selectedTools);
            Root.Children.Add(scope);
            toolsPanel.IsEnabled = agent?.Tools != null;
            Root.Children.Add(toolsPanel);

            toolsPanel.Children.Add(new TextBlock { Text = "Built-in", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 6) });
            var builtIn = new WrapPanel();
            foreach (var tool in AgentFile.BuiltInTools) builtIn.Children.Add(Chip(window, tool, tool));
            toolsPanel.Children.Add(builtIn);

            var servers = Wsl.ConfiguredMcpServers(distro);
            foreach (var server in servers)
            {
                var prefix = $"mcp__{server.Name}__";
                var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 6) };
                header.Children.Add(new TextBlock { Text = "MCP · " + server.Name, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                var chips = new WrapPanel();
                foreach (var tool in original.Where(t => t.StartsWith(prefix))) chips.Children.Add(Chip(window, tool, tool.Substring(prefix.Length)));
                if (server.Type == "http")
                {
                    var load = new Button { Content = "Load tools", Style = window.StyleOf("Secondary"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(10, 0, 0, 0), FontSize = 12 };
                    var status = new TextBlock { FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = window.BrushOf("Subtext") };
                    load.Click += async (s, e) =>
                    {
                        load.IsEnabled = false;
                        status.Text = "Loading …";
                        try
                        {
                            var tools = await McpClient.ListTools(server);
                            foreach (var tool in tools.Where(t => toolChips.All(c => (string)c.Tag != prefix + t.Name)))
                                chips.Children.Add(Chip(window, prefix + tool.Name, tool.Name, tool.Description));
                            status.Text = $"{tools.Count} tools";
                        }
                        catch (Exception ex)
                        {
                            status.Text = ex.InnerException?.Message ?? ex.Message;
                            status.Foreground = window.BrushOf("Red");
                            load.IsEnabled = true;
                        }
                    };
                    header.Children.Add(load);
                    header.Children.Add(status);
                }
                else
                {
                    header.Children.Add(new TextBlock { Text = "  tools can be added by name below", FontSize = 12, Foreground = window.BrushOf("Subtext"), VerticalAlignment = VerticalAlignment.Center });
                }
                toolsPanel.Children.Add(header);
                toolsPanel.Children.Add(chips);
            }

            // Servers defined in this agent only: their tools are granted as a whole with mcp__<server>.
            foreach (var own in agent?.McpServers.Keys ?? Enumerable.Empty<string>())
            {
                toolsPanel.Children.Add(new TextBlock { Text = $"MCP \u00b7 {own} \u00b7 only this agent", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6) });
                var ownChips = new WrapPanel();
                ownChips.Children.Add(Chip(window, "mcp__" + own, "All tools of " + own));
                toolsPanel.Children.Add(ownChips);
            }

            var known = new HashSet<string>(AgentFile.BuiltInTools.Concat(original.Where(t => servers.Any(s => t.StartsWith($"mcp__{s.Name}__"))))
                .Concat((agent?.McpServers.Keys ?? Enumerable.Empty<string>()).Select(k => "mcp__" + k)));
            toolsPanel.Children.Add(new TextBlock { Text = "Other tools, comma separated", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6) });
            otherTools = new TextBox { Text = string.Join(", ", original.Where(t => !known.Contains(t))), TextWrapping = TextWrapping.Wrap };
            toolsPanel.Children.Add(otherTools);

            if (agent == null)
            {
                Label("INSTRUCTIONS");
                Root.Children.Add(Hint("The system prompt of the agent: its role, rules and how it reports."));
                instructions = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    FontFamily = (System.Windows.Media.FontFamily)window.FindResource("Mono"), FontSize = 12 };
                Root.Children.Add(instructions);
            }
            else
            {
                Label("INSTRUCTIONS");
                var open = new Button { Content = "Open the file", Style = window.StyleOf("Secondary"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 7, 14, 7) };
                open.Click += (s, e) => Process.Start(new ProcessStartInfo(agent.Path) { UseShellExecute = true });
                Root.Children.Add(Hint("The instructions are the text below the header in the agent file."));
                Root.Children.Add(open);
            }
        }

        CheckBox Chip(MainWindow window, string tool, string label, string tooltip = null)
        {
            var chip = new CheckBox { Style = window.StyleOf("ChipToggle"), Content = label, Tag = tool, IsChecked = original.Contains(tool), ToolTip = tooltip };
            System.Windows.Automation.AutomationProperties.SetName(chip, tool);
            toolChips.Add(chip);
            return chip;
        }

        public string AgentName => name.Text.Trim();
        public string Description => description.Text.Trim();
        public string Model
        {
            get
            {
                var key = models.First(m => m.Value.IsChecked == true).Key;
                return key == "pinned" ? pinnedModel.Text.Trim() : key;
            }
        }
        public string Instructions => instructions?.Text ?? "";

        // Kept tools stay in their original order, newly selected ones are appended.
        public List<string> Tools
        {
            get
            {
                if (allTools.IsChecked == true) return null;
                var chosen = toolChips.Where(c => c.IsChecked == true).Select(c => (string)c.Tag)
                    .Concat(otherTools.Text.Split(',').Select(t => t.Trim()).Where(t => t != "")).Distinct().ToList();
                return original.Where(chosen.Contains).Concat(chosen.Where(t => !original.Contains(t))).ToList();
            }
        }

        public string Validate(string folder)
        {
            if (agent == null)
            {
                if (!Regex.IsMatch(AgentName, "^[a-z0-9][a-z0-9-]*$")) return "Name: lowercase letters, digits and dashes only.";
                if (File.Exists(Path.Combine(folder, AgentName + ".md"))) return $"An agent named {AgentName} already exists.";
                if (Instructions.Trim() == "") return "Write the instructions for the agent.";
            }
            if (Description == "") return "The description is missing; Claude needs it to decide when to use the agent.";
            if (models["pinned"].IsChecked == true && !Regex.IsMatch(Model, "^[a-z0-9][a-z0-9.\\[\\]-]*$"))
                return "Fixed version: enter a full model ID such as claude-opus-5-5.";
            if (Tools != null && Tools.Count == 0) return "Select at least one tool, or choose \"All tools\".";
            return null;
        }
    }
}
