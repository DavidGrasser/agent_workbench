using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ClaudeWorkflowSetup
{
    public sealed class ComponentItem : INotifyPropertyChanged
    {
        bool isChecked = true;
        public string Key;
        public string Title;
        public string Description;
        public string Group;
        public string[] Requires = new string[0];
        // The coding tools this option works with; empty means all of them.
        public string[] Tools = new string[0];
        public FrameworkElement Card;
        public bool Applicable = true;
        public Presence State = Presence.Unknown;
        public Border Badge;
        public TextBlock BadgeText;
        public TextBlock DetailText;

        public bool IsChecked
        {
            get => isChecked;
            set
            {
                if (isChecked == value) return;
                isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public partial class MainWindow : Window
    {
        enum Page { Welcome, Tools, Components, Settings, Wsl, Install, Done, Overview, McpManage, Agents, AgentEdit, Projects, Project, Config }
        enum WslMode { Install, Reboot, User }

        static readonly string[] StepNames = { "Welcome", "Coding tools", "Options", "Settings", "WSL", "Install", "Done" };
        static readonly string[] ToolKeys = { "claude", "codex", "opencode" };
        static readonly string[] WslComponents = { "shell", "claude", "codex", "opencode", "vault", "config", "mcp", "browser", "cron", "git", "terminal" };

        readonly Dictionary<string, string> manifest = Payload.Manifest();
        readonly List<McpServer> servers = Payload.McpServers();
        readonly bool packageHasVault = Payload.HasFolder("vault");
        readonly List<ComponentItem> items = new List<ComponentItem>();
        readonly Dictionary<string, TextBlock> groupLabels = new Dictionary<string, TextBlock>();
        readonly List<McpEditor> mcpEditors = new List<McpEditor>();
        readonly Dictionary<string, RadioButton> vaultSources = new Dictionary<string, RadioButton>();
        readonly DispatcherTimer userPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        readonly bool resumed;

        Page page;
        WslMode wslMode;
        SetupState state;
        List<string> distros = new List<string>();
        string selectedDistro;
        string logFile;
        bool installing;
        bool mcpInitialized;
        bool workflowTouched;
        Detection detection;

        public MainWindow(bool resume, bool manage)
        {
            InitializeComponent();
            WindowFit.Attach(this);
            BuildStepList();
            BuildWelcome();
            BuildComponents();
            BuildVaultSources();
            BuildWorkflowToggle();

            var settings = AppSettings.Load();
            VaultPath.Text = settings?.VaultPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"Documents\claude-vault");
            GitName.Text = Get("GIT_NAME");
            GitEmail.Text = Get("GIT_EMAIL");
            LinuxUser.Text = SuggestLinuxUser();
            userPoll.Tick += async (s, e) => await PollForUser();

            state = resume ? SetupState.Load() : null;
            resumed = state != null;
            if (resumed)
                Loaded += async (s, e) => { ShowPage(Page.Wsl); await CheckWsl(); };
            else if (manage)
                Loaded += async (s, e) => await ShowOverview(settings);
            else
                ShowPage(Page.Welcome);
        }

        string Get(string key) => manifest.TryGetValue(key, out var v) ? v : "";

        Brush Brush(string key) => (Brush)FindResource(key);

        // ---------------------------------------------------------------- layout

        void BuildStepList()
        {
            for (int i = 0; i < StepNames.Length; i++)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
                row.Children.Add(new Border
                {
                    Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 12, 0),
                    Child = new TextBlock { Text = (i + 1).ToString(), FontSize = 11, FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                });
                row.Children.Add(new TextBlock { Text = StepNames[i], FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
                StepList.Children.Add(row);
            }
        }

        void UpdateStepList()
        {
            bool managing = page >= Page.Overview;
            StepList.Visibility = managing ? Visibility.Collapsed : Visibility.Visible;
            NavList.Visibility = managing ? Visibility.Visible : Visibility.Collapsed;
            ModeLabel.Text = managing ? "Configuration" : "Setup";
            for (int i = 0; i < StepList.Children.Count; i++)
            {
                var row = (StackPanel)StepList.Children[i];
                var dot = (Border)row.Children[0];
                var number = (TextBlock)dot.Child;
                var label = (TextBlock)row.Children[1];
                bool current = i == (int)page, done = i < (int)page;
                dot.Background = current ? Brush("Accent") : done ? Brush("Green") : Brush("Surface0");
                number.Text = done ? "✓" : (i + 1).ToString();
                number.Foreground = current || done ? Brush("Crust") : Brush("Subtext");
                label.Foreground = current ? Brush("Text") : Brush("Subtext");
                label.FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        void BuildWelcome()
        {
            PackageInfo.Text = $"Package from {Get("EXPORTED_FROM")}\n{Get("EXPORTED_AT")}";
            void Fact(string label, string value)
            {
                var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.Children.Add(new TextBlock { Text = label, Foreground = Brush("Subtext"), FontSize = 13 });
                var v = new TextBlock { Text = value, FontSize = 13 };
                Grid.SetColumn(v, 1);
                g.Children.Add(v);
                WelcomeFacts.Children.Add(g);
            }
            Fact("Exported", $"{Get("EXPORTED_AT")} on {Get("EXPORTED_FROM")}");
            Fact("Claude Code", Get("CLAUDE_VERSION"));
            Fact("Vault", packageHasVault ? "personal vault included, plus a template for a new one" : "template for a new vault");
            Fact("MCP servers", servers.Count > 0 ? string.Join(", ", servers.Select(s => s.Name)) : "none");
        }

        void BuildComponents()
        {
            void Add(string group, string key, string title, string description, string[] tools, params string[] requires) =>
                items.Add(new ComponentItem { Group = group, Key = key, Title = title, Description = description, Tools = tools, Requires = requires });
            var all = new string[0];
            var claude = new[] { "claude" };

            Add("TOOLS", "claude", "Claude Code", "Anthropic's coding agent. Agent teams in tmux panes, hooks, auto-memory, Chrome extension. Sign in with your Claude account.", all);
            Add("TOOLS", "codex", "Codex CLI", "OpenAI's coding agent. Reads the shared workflow core (AGENTS.md) from the vault. Sign in with ChatGPT or an API key.", all);
            Add("TOOLS", "opencode", "OpenCode", "Open-source coding agent for any model provider. Reads the shared workflow core and gets the vault's agents as subagents. Sign in with /connect.", all);
            Add("CLAUDE CODE", "config", "Claude configuration",
                "Links agents, commands, hooks and the workflow from the vault into ~/.claude, plus settings.json and skills.", claude, "vault");
            Add("CLAUDE CODE", "chrome", "Chrome extension",
                "Opens the store page of \"Claude in Chrome\" at the end, one click on Add is enough.", claude);
            Add("CLAUDE CODE", "cron", "Monthly /insights report", "A cron job that creates an /insights report once a month. Its script comes from the vault.", claude, "claude", "config");
            Add("TERMINAL", "shell", "Terminal environment",
                "tmux with Catppuccin, Starship prompt and a launcher per tool that opens it in its own tmux session.", all);
            Add("TERMINAL", "terminal", "Windows integration",
                "A Windows Terminal profile and an \"Open in …\" entry in the folder context menu per selected tool, plus the Nerd Font.", all, "shell");
            Add("KNOWLEDGE", "vault", "Obsidian vault",
                "Long-term memory and the shared instructions of your coding tools, and the home of your agents. Restore yours, start a new one or use an existing folder.", all);
            Add("KNOWLEDGE", "obsidian", "Obsidian", "Installs Obsidian and registers the vault, with all plugins of the vault active.", all, "vault");
            Add("CONNECTIONS", "mcp", "MCP servers", (servers.Count > 0
                ? $"Takes over {string.Join(", ", servers.Select(x => x.Name))} from the package. Edit, remove or add your own in the next step."
                : "Add your own servers in the next step instead of editing the configuration by hand.") + " Written into every selected tool.", all);
            Add("CONNECTIONS", "browser", "Browser for Codex and OpenCode",
                "Playwright MCP, so Codex and OpenCode can open pages, click and read them like Claude in Chrome. Installs Node.js 22 (replacing an older Node.js) and Google Chrome inside WSL; the browser window appears on the Windows desktop.",
                new[] { "codex", "opencode" });
            Add("EXTRAS", "git", "Git identity", "Name and email for commits, if not set in WSL yet.", all);

            foreach (var group in items.GroupBy(i => i.Group))
            {
                var target = group.Key == "TOOLS" ? ToolCards : ComponentGroups;
                var label = new TextBlock { Text = group.Key, Style = (Style)FindResource("GroupLabel") };
                if (group.Key != "TOOLS") target.Children.Add(label);
                groupLabels[group.Key] = label;
                foreach (var item in group)
                {
                    var card = new CheckBox { Style = (Style)FindResource("Card"), Content = CardContent(item) };
                    System.Windows.Automation.AutomationProperties.SetName(card, item.Title);
                    card.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                        new Binding(nameof(ComponentItem.IsChecked)) { Source = item, Mode = BindingMode.TwoWay });
                    item.PropertyChanged += (s, e) => ApplyDependencies(item);
                    item.Card = card;
                    target.Children.Add(card);
                }
            }
        }

        FrameworkElement CardContent(ComponentItem item)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = item.Title, FontSize = 14, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = item.Description, FontSize = 12, Foreground = Brush("Subtext"), Margin = new Thickness(0, 3, 0, 0) });
            item.DetailText = new TextBlock { FontSize = 12, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
            text.Children.Add(item.DetailText);
            grid.Children.Add(text);
            item.BadgeText = new TextBlock { Text = "Checking …", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brush("Subtext") };
            item.Badge = new Border { Child = item.BadgeText, CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 2, 9, 3),
                Background = Brush("Surface1"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(item.Badge, 1);
            grid.Children.Add(item.Badge);
            return grid;
        }

        void BuildVaultSources()
        {
            var options = new List<(string Key, string Label)>();
            if (packageHasVault) options.Add(("package", "Restore from this package"));
            options.Add(("template", "Start a new vault"));
            options.Add(("existing", "Use an existing folder"));
            foreach (var (key, label) in options)
            {
                var chip = new RadioButton { Style = (Style)FindResource("Chip"), Content = label, GroupName = "vaultsource", IsChecked = key == options[0].Key };
                chip.Checked += (s, e) => UpdateVaultNote();
                vaultSources[key] = chip;
                VaultSourceChips.Children.Add(chip);
            }
        }

        void BuildWorkflowToggle()
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = "Add the generic coding workflow", FontSize = 14, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { FontSize = 12, Foreground = Brush("Subtext"), Margin = new Thickness(0, 3, 0, 0),
                Text = "Agents coder, reviewer, verifier, tracer and ui-inspector plus the instructions in workflow/: a team that codes, reviews and verifies, " +
                       "for any language. AGENTS.md is shared by all tools, CLAUDE.md adds the Claude Code team mechanics. " +
                       "Copied into the vault, existing files are never overwritten. Leave it off if your vault brings its own." });
            WorkflowToggle.Content = text;
            System.Windows.Automation.AutomationProperties.SetName(WorkflowToggle, "Add the generic coding workflow");
        }

        void WorkflowToggle_Click(object sender, RoutedEventArgs e) => workflowTouched = true;

        // Default: a vault that already has agents keeps its own workflow.
        void UpdateWorkflowDefault(VaultInfo info)
        {
            WorkflowToggle.Visibility = Checked("vault") || Checked("config") ? Visibility.Visible : Visibility.Collapsed;
            if (workflowTouched) return;
            var source = VaultSource;
            WorkflowToggle.IsChecked = source != "package" && info.Agents.Count == 0;
        }

        string VaultSource => Checked("vault") ? vaultSources.First(v => v.Value.IsChecked == true).Key : "existing";

        // ------------------------------------------------------------- detection

        async Task DetectAsync()
        {
            NextButton.IsEnabled = false;
            var path = VaultPath.Text.Trim();
            detection = await Task.Run(() => Detection.Run(path));
            WslStatus.Text = "WSL: " + detection.WslSummary;
            selectedDistro = selectedDistro ?? detection.Distro;
            foreach (var item in items)
            {
                var finding = detection.Components.TryGetValue(item.Key, out var f) ? f : new Finding { State = Presence.Missing };
                item.State = finding.State;
                item.BadgeText.Text = finding.State == Presence.Present ? "Installed" : finding.State == Presence.Partial ? "Partial" : "New";
                item.Badge.Background = finding.State == Presence.Present ? Brush("Green") : finding.State == Presence.Partial ? Brush("Yellow") : Brush("Surface1");
                item.BadgeText.Foreground = finding.State == Presence.Missing ? Brush("Subtext") : Brush("Crust");
                item.DetailText.Text = finding.Detail == "" ? "" : "Found: " + finding.Detail;
                item.DetailText.Foreground = finding.State == Presence.Present ? Brush("Green") : Brush("Yellow");
                item.DetailText.Visibility = finding.Detail == "" ? Visibility.Collapsed : Visibility.Visible;
            }
            // A tool means "use it": the ones on this PC are preselected, Claude Code when there is none.
            // Options are suggested when missing; FilterOptions drops the ones the chosen tools cannot use.
            var present = ToolKeys.Where(k => items.First(i => i.Key == k).State == Presence.Present).ToList();
            if (present.Count == 0) present.Add("claude");
            foreach (var item in items)
                item.IsChecked = ToolKeys.Contains(item.Key) ? present.Contains(item.Key) : item.State != Presence.Present;
            NextButton.IsEnabled = true;
        }

        // A dependency that is already on this PC counts as satisfied.
        void ApplyDependencies(ComponentItem changed)
        {
            if (changed.IsChecked)
            {
                foreach (var dependency in items.Where(i => changed.Requires.Contains(i.Key) && i.State != Presence.Present))
                    dependency.IsChecked = true;
            }
            else if (changed.State != Presence.Present)
            {
                foreach (var dependent in items.Where(i => i.Requires.Contains(changed.Key))) dependent.IsChecked = false;
            }
        }

        bool Checked(string key) => items.Any(i => i.Key == key && i.IsChecked);

        // Shows only the options that work with a chosen tool. An option that comes back into view gets
        // its default again; one that leaves is deselected so it cannot be installed unseen.
        void FilterOptions()
        {
            var chosen = ToolKeys.Where(Checked).ToList();
            foreach (var item in items.Where(i => !ToolKeys.Contains(i.Key)))
            {
                bool applicable = item.Tools.Length == 0 || item.Tools.Any(chosen.Contains);
                if (applicable && !item.Applicable) item.IsChecked = item.State != Presence.Present;
                if (!applicable) item.IsChecked = false;
                item.Applicable = applicable;
                item.Card.Visibility = applicable ? Visibility.Visible : Visibility.Collapsed;
            }
            // Terminal profiles and MCP servers are written per tool: "installed" only covers the tools
            // already here, so a newly chosen tool needs both steps again.
            var added = items.Where(i => chosen.Contains(i.Key) && i.State != Presence.Present).Select(i => i.Title).ToList();
            foreach (var item in items.Where(i => (i.Key == "terminal" || i.Key == "mcp" || i.Key == "browser") && i.Applicable && i.State == Presence.Present && added.Count > 0))
                item.IsChecked = true;
            foreach (var group in items.Where(i => !ToolKeys.Contains(i.Key)).GroupBy(i => i.Group))
                groupLabels[group.Key].Visibility = group.Any(i => i.Applicable) ? Visibility.Visible : Visibility.Collapsed;
            var names = items.Where(i => chosen.Contains(i.Key)).Select(i => i.Title).ToList();
            ComponentsLead.Text = $"Options for {JoinNames(names)}. Anything already on this PC stays unselected; select it to set it up again. Dependencies are selected automatically." +
                (added.Count > 0 ? $" Windows integration and MCP servers are selected so that {JoinNames(added)} gets them too." : "");
            ComponentScroll.ScrollToTop();
        }

        static string JoinNames(List<string> names) =>
            names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names.Last();
        bool NeedsWsl => WslComponents.Any(Checked);
        bool NeedsVaultPath => Checked("vault") || Checked("config") || Checked("obsidian");

        void ShowPage(Page target)
        {
            page = target;
            var pages = new FrameworkElement[] { PageWelcome, PageTools, PageComponents, PageSettings, PageWsl, PageInstall, PageDone, PageOverview, PageMcp, PageAgents, PageAgentEdit, PageProjects, PageProjects, PageConfig };
            foreach (var element in pages.Distinct()) element.Visibility = element == pages[(int)target] ? Visibility.Visible : Visibility.Collapsed;
            UpdateStepList();
            FooterNote.Text = "";
            BackButton.Visibility = target == Page.Tools || target == Page.Components || target == Page.Settings || (target == Page.Wsl && !resumed) || target == Page.AgentEdit || target == Page.Project
                ? Visibility.Visible : Visibility.Collapsed;
            BackButton.Content = target == Page.AgentEdit ? "Cancel" : "Back";
            NextButton.Visibility = target == Page.Wsl || target == Page.Install ? Visibility.Collapsed : Visibility.Visible;
            NextButton.Content = target == Page.Welcome ? "Let's go" : target == Page.Settings ? "Install"
                : target == Page.AgentEdit ? "Save" : target == Page.Config ? "Save changes"
                : target == Page.Done || target >= Page.Overview ? "Close" : "Next";
        }

        // ------------------------------------------------------------ navigation

        void Back_Click(object sender, RoutedEventArgs e)
        {
            userPoll.Stop();
            if (page == Page.AgentEdit) { Go(ShowAgents); return; }
            if (page == Page.Project) { ShowProjects(); return; }
            ShowPage(page == Page.Wsl ? Page.Settings : page - 1);
        }

        async void Next_Click(object sender, RoutedEventArgs e)
        {
            switch (page)
            {
                case Page.Welcome:
                    ShowPage(Page.Tools);
                    if (detection == null) await DetectAsync();
                    break;
                case Page.Tools:
                    if (!ToolKeys.Any(Checked)) { FooterNote.Text = "Pick at least one coding tool."; return; }
                    FilterOptions();
                    ShowPage(Page.Components);
                    break;
                case Page.Components:
                    PrepareSettings();
                    ShowPage(Page.Settings);
                    break;
                case Page.Settings:
                    var error = ValidateSettings();
                    if (error != null) { FooterNote.Text = error; return; }
                    BuildState();
                    if (NeedsWsl) { ShowPage(Page.Wsl); await CheckWsl(); }
                    else await StartInstall();
                    break;
                case Page.AgentEdit:
                    await SaveAgent();
                    break;
                case Page.Config:
                    await SaveConfig();
                    break;
                case Page.Done:
                case Page.Overview:
                case Page.McpManage:
                case Page.Agents:
                case Page.Projects:
                case Page.Project:
                    Close();
                    break;
            }
        }

        void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        void Maximize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        // WindowFit keeps the maximized window inside the work area. The app remembers the state
        // for its next start.
        void Window_StateChanged(object sender, EventArgs e)
        {
            bool maximized = WindowState == WindowState.Maximized;
            MaximizeIcon.Data = Geometry.Parse(maximized ? "M2,0 L10,0 L10,8 M0,2 L8,2 L8,10 L0,10 Z" : "M0,0 L10,0 L10,10 L0,10 Z");
            if (appSettings != null && WindowState != WindowState.Minimized)
            {
                appSettings.Maximized = maximized;
                appSettings.Save();
            }
        }

        void Close_Click(object sender, RoutedEventArgs e)
        {
            if (installing && MessageBox.Show(this, "The installation is still running. Cancel it?", "Agent Workbench",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Close();
        }

        // -------------------------------------------------------------- settings

        void PrepareSettings()
        {
            DistroSection.Visibility = NeedsWsl ? Visibility.Visible : Visibility.Collapsed;
            if (NeedsWsl)
            {
                distros = detection?.Distros ?? Wsl.Distros();
                selectedDistro = selectedDistro != null && distros.Contains(selectedDistro) ? selectedDistro : Wsl.Pick(distros);
                DistroChips.Children.Clear();
                foreach (var d in distros)
                {
                    var chip = new RadioButton { Style = (Style)FindResource("Chip"), Content = d, IsChecked = d == selectedDistro, GroupName = "distro" };
                    chip.Checked += (s, e) => selectedDistro = d;
                    DistroChips.Children.Add(chip);
                }
                DistroNote.Text = distros.Count == 0
                    ? "There is no WSL on this PC yet. Ubuntu is installed in the next step."
                    : distros.Count > 1 ? "The coding tools are installed into this distribution." : "";
            }

            VaultSection.Visibility = NeedsVaultPath ? Visibility.Visible : Visibility.Collapsed;
            VaultSourceChips.Visibility = Checked("vault") ? Visibility.Visible : Visibility.Collapsed;
            UpdateVaultNote();
            GitSection.Visibility = Checked("git") ? Visibility.Visible : Visibility.Collapsed;

            McpSection.Visibility = Checked("mcp") ? Visibility.Visible : Visibility.Collapsed;
            var files = new List<string>();
            if (Checked("claude")) files.Add("~/.claude.json");
            if (Checked("codex")) files.Add("~/.codex/config.toml");
            if (Checked("opencode")) files.Add("~/.config/opencode/opencode.json");
            McpSetupNote.Text = "Servers from the package are prefilled. Change them, remove them or add your own. " +
                                $"They are stored only inside WSL, in {JoinNames(files)}.";
            if (!mcpInitialized)
            {
                foreach (var server in servers) AddMcpEditor(server);
                mcpInitialized = true;
            }
            SettingsScroll.ScrollToTop();
        }

        void UpdateVaultNote()
        {
            if (VaultNote == null) return;
            var info = VaultInfo.Scan(VaultPath.Text.Trim());
            var source = VaultSource;
            VaultSourceNote.Text =
                source == "package" ? "Copies the vault from this package, with all memories, notes, agents and Obsidian plugins."
                : source == "template" ? "Creates an empty vault with the folder layout, the Obsidian plugins and the generic hooks."
                : Checked("vault") ? "Uses the vault in this folder as it is, for example one you copied from your old PC."
                : "Your coding tools use the vault in this folder.";

            bool copies = source == "package" || source == "template";
            bool occupied = info.Exists && Directory.EnumerateFileSystemEntries(info.Path).Any();
            if (copies && occupied)
            {
                VaultNote.Text = "This folder is not empty, so nothing is copied into it. Choose an empty folder or \"Use an existing folder\".";
                VaultNote.Foreground = Brush("Yellow");
            }
            else if (copies)
            {
                VaultNote.Text = "The vault is created in this folder.";
                VaultNote.Foreground = Brush("Subtext");
            }
            else
            {
                VaultNote.Text = info.LooksLikeVault ? info.Summary : info.Exists ? "No vault found in this folder." : "This folder does not exist.";
                VaultNote.Foreground = info.LooksLikeVault ? Brush("Green") : Brush("Red");
            }
            VaultAgents.Text = !copies && info.Agents.Count > 0 ? "Agents: " + string.Join(", ", info.Agents.Select(a => a.Name)) : "";
            UpdateWorkflowDefault(info);
        }

        void AddMcpEditor(McpServer server)
        {
            McpEditor editor = null;
            editor = new McpEditor(this, server, () =>
            {
                mcpEditors.Remove(editor);
                McpFields.Children.Remove(editor.Root);
            });
            mcpEditors.Add(editor);
            McpFields.Children.Add(editor.Root);
        }

        void AddMcp_Click(object sender, RoutedEventArgs e) => AddMcpEditor(new McpServer());

        internal static async Task<(bool, string)> ProbeMcp(McpServer server)
        {
            if (!Uri.TryCreate(server.Url.Trim(), UriKind.Absolute, out var uri)) return (false, "Enter a valid URL first.");
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                using (var request = new HttpRequestMessage(server.Type == "sse" ? HttpMethod.Get : HttpMethod.Post, uri))
                {
                    if (server.Header.Trim() != "" && server.Secret != "")
                        request.Headers.TryAddWithoutValidation(server.Header.Trim(), server.AuthValue);
                    request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
                    if (server.Type != "sse")
                        request.Content = new StringContent(
                            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\"," +
                            "\"capabilities\":{},\"clientInfo\":{\"name\":\"agent-workbench\",\"version\":\"1.0\"}}}",
                            Encoding.UTF8, "application/json");
                    using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                    {
                        int code = (int)response.StatusCode;
                        if (response.IsSuccessStatusCode) return (true, "✓ Connected, the server answers.");
                        if (code == 401 || code == 403) return (false, "Login rejected, check the token or the header.");
                        return (false, $"The server answers with {code}.");
                    }
                }
            }
            catch (Exception e)
            {
                return (false, "Not reachable: " + (e.InnerException?.Message ?? e.Message));
            }
        }

        string ValidateSettings()
        {
            var path = VaultPath.Text.Trim();
            if (NeedsVaultPath && !Path.IsPathRooted(path)) return "Enter a full folder path for the vault.";
            if (NeedsVaultPath && VaultSource == "existing" && !VaultInfo.Scan(path).Exists) return "The vault folder does not exist.";
            if (Checked("git") && (GitName.Text.Trim() == "" || GitEmail.Text.Trim() == "")) return "Enter name and email for Git.";
            if (Checked("mcp"))
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var editor in mcpEditors)
                {
                    var error = editor.Validate();
                    if (error != null) return error;
                    if (!names.Add(editor.Read().Name)) return $"The name {editor.Read().Name} is used twice.";
                }
            }
            return null;
        }

        void BuildState()
        {
            var mcp = mcpEditors.Select(m => m.Read()).ToDictionary(m => m.Name, m => (object)m.ToConfig());
            state = new SetupState
            {
                Components = items.Where(i => i.IsChecked).Select(i => i.Key).ToList(),
                McpServersJson = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(Checked("mcp") ? mcp : new Dictionary<string, object>()),
                VaultPath = NeedsVaultPath ? VaultPath.Text.Trim() : null,
                VaultSource = NeedsVaultPath ? VaultSource : null,
                AddWorkflow = Checked("config") && WorkflowToggle.IsChecked == true,
                UseChrome = Checked("chrome") || items.Any(i => i.Key == "chrome" && i.State == Presence.Present),
                Distro = selectedDistro ?? "Ubuntu",
                GitName = GitName.Text.Trim(),
                GitEmail = GitEmail.Text.Trim(),
            };
        }

        void VaultPath_TextChanged(object sender, TextChangedEventArgs e) => UpdateVaultNote();

        void BrowseVault_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose the folder of the Obsidian vault" })
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                var chosen = dialog.SelectedPath;
                bool keep = VaultSource == "existing" || VaultInfo.Scan(chosen).LooksLikeVault || Path.GetFileName(chosen) == "claude-vault";
                VaultPath.Text = keep ? chosen : Path.Combine(chosen, "claude-vault");
            }
        }

        // ------------------------------------------------------------------- WSL

        async Task CheckWsl()
        {
            SetWslBusy("Checking WSL …");
            distros = await Task.Run(() => Wsl.Distros());
            if (distros.Count == 0)
            {
                SetWslMode(WslMode.Install);
                return;
            }
            if (!distros.Contains(state.Distro)) state.Distro = Wsl.Pick(distros);
            if (await Task.Run(() => Wsl.HasUser(state.Distro)))
            {
                await StartInstall();
                return;
            }
            SetWslMode(WslMode.User);
            userPoll.Start();
        }

        void SetWslBusy(string text)
        {
            WslBusy.Text = text;
            WslAction.IsEnabled = text == "";
        }

        void SetWslMode(WslMode mode)
        {
            wslMode = mode;
            SetWslBusy("");
            WslUserForm.Visibility = mode == WslMode.User ? Visibility.Visible : Visibility.Collapsed;
            switch (mode)
            {
                case WslMode.Install:
                    WslTitle.Text = resumed ? "Finish setting up Ubuntu" : "Set up WSL";
                    WslText.Text = "Linux on Windows (WSL) is not set up on this PC yet. The setup installs Ubuntu now. " +
                                   "Windows asks for administrator rights. If a restart is needed, the setup continues here on its own afterwards.";
                    WslAction.Content = "Install Ubuntu";
                    break;
                case WslMode.Reboot:
                    WslTitle.Text = "Restart once";
                    WslText.Text = "WSL is installed, Windows needs one restart for it. Afterwards this setup opens on its own " +
                                   "and continues with your selection.";
                    WslAction.Content = "Restart now";
                    break;
                case WslMode.User:
                    WslTitle.Text = "Create a Linux user";
                    WslText.Text = $"{state.Distro} is installed but has no user yet. Create one here. " +
                                   "You need the password later for sudo.";
                    WslAction.Content = "Create user";
                    break;
            }
        }

        void WslLogLine(string line) => Dispatcher.BeginInvoke(new Action(() =>
        {
            WslLog.Visibility = Visibility.Visible;
            WslLog.AppendText(line + "\n");
            WslLog.ScrollToEnd();
        }));

        async void WslAction_Click(object sender, RoutedEventArgs e)
        {
            FooterNote.Text = "";
            switch (wslMode)
            {
                case WslMode.Install: await InstallWsl(); break;
                case WslMode.Reboot: Process.Start("shutdown.exe", "/r /t 3"); Close(); break;
                case WslMode.User: await CreateLinuxUser(); break;
            }
        }

        async Task InstallWsl()
        {
            SetWslBusy("Installing Ubuntu, this can take a few minutes …");
            try
            {
                var p = Process.Start(new ProcessStartInfo("wsl.exe", "--install -d Ubuntu --no-launch") { UseShellExecute = true, Verb = "runas" });
                await Task.Run(() => p.WaitForExit());
            }
            catch (Win32Exception)
            {
                SetWslMode(WslMode.Install);
                FooterNote.Text = "WSL cannot be installed without administrator rights.";
                return;
            }

            distros = await Task.Run(() => Wsl.Distros());
            if (distros.Count == 0)
                // Store-packaged Ubuntu registers itself only on first launch.
                distros = await Task.Run(() => { Shell.Capture("ubuntu.exe", "install --root", 600000); return Wsl.Distros(); });

            if (distros.Count > 0)
            {
                state.Distro = Wsl.Pick(distros);
                await CheckWsl();
                return;
            }
            state.Save();
            RegisterResume();
            SetWslMode(WslMode.Reboot);
        }

        void RegisterResume()
        {
            var copy = Path.Combine(Payload.WorkDir, "Agent-Workbench-Setup.exe");
            var self = Process.GetCurrentProcess().MainModule.FileName;
            if (!string.Equals(self, copy, StringComparison.OrdinalIgnoreCase)) File.Copy(self, copy, true);
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce"))
                key.SetValue("ClaudeWorkflowSetup", $"\"{copy}\" --resume");
        }

        async Task CreateLinuxUser()
        {
            var user = LinuxUser.Text.Trim();
            if (!Regex.IsMatch(user, "^[a-z_][a-z0-9_-]{0,31}$")) { FooterNote.Text = "User name: lowercase letters, digits, - and _ only."; return; }
            if (LinuxPassword.Password == "" || LinuxPassword.Password != LinuxPassword2.Password) { FooterNote.Text = "The passwords do not match."; return; }
            userPoll.Stop();
            SetWslBusy("Creating the user …");
            int code = await Wsl.CreateUser(state.Distro, user, LinuxPassword.Password, WslLogLine);
            if (code == 0 && await Task.Run(() => Wsl.HasUser(state.Distro)))
            {
                await StartInstall();
                return;
            }
            SetWslMode(WslMode.User);
            FooterNote.Text = $"The user could not be created (code {code}), details above.";
        }

        async Task PollForUser()
        {
            if (page != Page.Wsl || wslMode != WslMode.User || !WslAction.IsEnabled) return;
            if (await Task.Run(() => Wsl.HasUser(state.Distro)))
            {
                userPoll.Stop();
                await StartInstall();
            }
        }

        static string SuggestLinuxUser()
        {
            var name = Regex.Replace(Environment.UserName.ToLowerInvariant(), "[^a-z0-9_-]", "");
            return Regex.IsMatch(name, "^[a-z_]") ? name : "user";
        }

        // --------------------------------------------------------------- install

        async Task StartInstall()
        {
            userPoll.Stop();
            ShowPage(Page.Install);
            installing = true;
            Directory.CreateDirectory(Payload.WorkDir);
            logFile = Path.Combine(Payload.WorkDir, $"install-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            var installer = new Installer(state, AppendLog,
                step => Dispatcher.BeginInvoke(new Action(() => CurrentStep.Text = step)));
            installer.Progress += p => Dispatcher.BeginInvoke(new Action(() =>
            {
                Progress.Value = p;
                ProgressText.Text = $"{(int)(p * 100)} %";
            }));

            bool ok;
            try
            {
                ok = await installer.RunAsync();
            }
            catch (Exception ex)
            {
                AppendLog("  [fail] " + ex.Message);
                ok = false;
            }
            installing = false;
            SetupState.Clear();
            if (ok && state.Has("chrome")) Installer.OpenChromeExtension();
            ShowDone(ok, installer.Warnings);
        }

        void AppendLog(string line)
        {
            File.AppendAllText(logFile, line + Environment.NewLine);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LogBox.AppendText(line + "\n");
                LogBox.ScrollToEnd();
            }));
        }

        void ShowDone(bool ok, int warnings)
        {
            ShowPage(Page.Done);
            DoneBadge.Background = !ok ? Brush("Red") : warnings > 0 ? Brush("Yellow") : Brush("Green");
            DoneIcon.Data = Geometry.Parse(ok ? "M 0 9 L 7 16 L 21 2" : "M 0 0 L 16 16 M 16 0 L 0 16");
            DoneTitle.Text = !ok ? "Installation stopped" : warnings > 0 ? $"Done, with {warnings} warning{(warnings == 1 ? "" : "s")}" : "Done.";
            DoneText.Text = !ok
                ? "A step failed, the log shows which one. It is safe to run the setup again."
                : warnings > 0
                    ? "Everything is set up, but some points need a look at the log."
                    : "Your coding environment is ready. A few things only you can do:";

            DoneActions.Children.Clear();
            if (ok && state.Has("claude"))
                AddAction(DoneActions, "Sign in to Claude", "Starts Claude in the terminal. On the first start you sign in in the browser.",
                    "Start Claude", () => Installer.StartClaude(state.Distro));
            foreach (var tool in Installer.OtherTools)
                if (ok && state.Has(tool.Key))
                    AddAction(DoneActions, $"Sign in to {tool.Name}", tool.Key == "codex"
                        ? "Starts Codex. On the first start it asks you to sign in with ChatGPT or an API key. Then type /hooks and trust the hooks, Codex skips them until then."
                        : "Starts OpenCode. Type /connect to choose a provider and sign in.",
                        $"Start {tool.Name}", () => Installer.StartTool(state.Distro, tool.Key, tool.Name));
            if (ok && state.Has("chrome"))
                AddAction(DoneActions, "Chrome extension", "The store page is open: click \"Add to Chrome\" and sign in inside the extension.",
                    "Open again", Installer.OpenChromeExtension);
            if (ok && state.Has("obsidian") && File.Exists(Installer.ObsidianExe))
                AddAction(DoneActions, "Obsidian", "On the first start choose \"Trust author and enable plugins\", otherwise the plugins stay off.",
                    "Open vault", () => Installer.OpenObsidian(state.VaultPath));
            if (ok)
                AddAction(DoneActions, "Agent Workbench", "Installed in the start menu. Open it later to see and manage your setup.",
                    "Open", () => Process.Start(AppSettings.InstalledExe));
            AddAction(DoneActions, "Log", logFile, "Show", () => Process.Start("notepad.exe", $"\"{logFile}\""));
        }

        void AddAction(Panel target, string title, string description, string button, Action action)
        {
            var card = new Border { Background = Brush("Surface0"), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 12, 12), Margin = new Thickness(0, 0, 0, 8) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            text.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = Brush("Subtext"), Margin = new Thickness(0, 2, 0, 0) });
            grid.Children.Add(text);
            if (button != null)
            {
                var b = new Button { Content = button, Style = (Style)FindResource("Secondary"), Padding = new Thickness(14, 7, 14, 7), VerticalAlignment = VerticalAlignment.Center };
                b.Click += (s, e) => action();
                Grid.SetColumn(b, 1);
                grid.Children.Add(b);
            }
            card.Child = grid;
            target.Children.Add(card);
        }

        // -------------------------------------------------------------- overview

        async Task ShowOverview(AppSettings settings)
        {
            appSettings = settings;
            if (NavList.Children.Count == 0)
            {
                BuildNav();
                if (settings.Maximized) WindowState = WindowState.Maximized;
            }
            ShowPage(Page.Overview);
            MarkNav(Page.Overview);
            PackageInfo.Text = $"Installed {settings.InstalledAt}";
            OverviewLead.Text = "What your coding tools use on this PC. Manage MCP servers, agents, projects and settings in the sections on the left.";
            OverviewPanel.Children.Clear();
            OverviewPanel.Children.Add(new TextBlock { Text = "Loading …", Foreground = Brush("Subtext") });

            var vault = await Task.Run(() => VaultInfo.Scan(settings.VaultPath));
            var probe = await Task.Run(() => OverviewProbe(settings.Distro));
            var tools = new[] { ("claude", "Claude Code"), ("codex", "Codex"), ("opencode", "OpenCode") };
            var servers = new Dictionary<string, List<McpServer>>();
            foreach (var (key, _) in tools.Where(t => probe.ContainsKey(t.Item1)))
            {
                try { servers[key] = await Task.Run(() => ListMcp(key)); }
                catch { servers[key] = null; }
            }
            OverviewPanel.Children.Clear();

            Section("CODING TOOLS");
            var toolTiles = new WrapPanel();
            foreach (var (key, name) in tools)
            {
                bool present = probe.TryGetValue(key, out var version);
                Action start = key == "claude" ? (Action)(() => Installer.StartClaude(settings.Distro)) : () => Installer.StartTool(settings.Distro, key, name);
                toolTiles.Children.Add(present
                    ? Tile(name, version, "Click to start it in Windows Terminal.", start, 260)
                    : Tile(name, "Not installed", "Add it with \"Run setup again\" below.", RunSetupAgain, 260));
            }
            OverviewPanel.Children.Add(toolTiles);

            Section("VAULT");
            var vaultActions = new List<(string, Action)>();
            if (vault.Exists)
            {
                vaultActions.Add(("Open in Obsidian", () => Installer.OpenObsidian(settings.VaultPath)));
                vaultActions.Add(("Open folder", () => Process.Start("explorer.exe", $"\"{settings.VaultPath}\"")));
            }
            ActionCard(OverviewPanel, settings.VaultPath ?? "No vault configured", vault.LooksLikeVault ? vault.Summary : "No vault found in this folder.", vaultActions.ToArray());

            var projects = Projects.FromVault(settings.VaultPath);
            Section($"PROJECTS ({projects.Count})");
            if (projects.Count == 0) Hint("No projects yet. Create one on the Projects page.");
            else OverviewPanel.Children.Add(ProjectTiles(projects, false));

            // Agents as Claude Code sees them everywhere on this PC: the links in ~/.claude/agents.
            var global = probe.Where(kv => kv.Key.StartsWith("agent:")).Select(kv => (Name: kv.Key.Substring(6), Source: kv.Value)).OrderBy(a => a.Name).ToList();
            var sets = Projects.Sets(settings.VaultPath);
            var globalSets = global.Select(a => a.Source).Where(src => src.Contains("/project-agents/"))
                .Select(src => src.Split(new[] { "/project-agents/" }, StringSplitOptions.None)[1].Split('/')[0]).Distinct().ToList();
            Section("AGENTS");
            var agentTiles = new WrapPanel();
            agentTiles.Children.Add(Tile($"Everywhere on this PC ({global.Count})",
                global.Count == 0 ? "No global agents" : (globalSets.Count > 0 ? "incl. set " + string.Join(", ", globalSets) : "from agents/"),
                string.Join(", ", global.Select(a => a.Name)), ShowAgents, 400));
            foreach (var set in sets.Keys.Where(k => !globalSets.Contains(k)))
                agentTiles.Children.Add(Tile($"Set {set} ({sets[set].Count})", "Linked per project", string.Join(", ", sets[set]), ShowAgents, 400));
            OverviewPanel.Children.Add(agentTiles);

            Section("MCP SERVERS");
            var mcpTiles = new WrapPanel();
            foreach (var (key, name) in tools.Where(t => servers.ContainsKey(t.Item1)))
            {
                var list = servers[key];
                var target = key;
                mcpTiles.Children.Add(Tile($"{name} ({list?.Count ?? 0})", list == null ? "Could not be read" : list.Count == 0 ? "No servers" : null,
                    list == null ? null : string.Join("\n", list.Select(x => $"{x.Name} \u00b7 {x.Summary}")), async () => await ShowMcpManage(target), 400));
            }
            OverviewPanel.Children.Add(mcpTiles);

            var again = new Button { Content = "Run setup again", Style = (Style)FindResource("Secondary"), HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 18, 0, 0) };
            again.Click += (s, e) => RunSetupAgain();
            OverviewPanel.Children.Add(again);

            void Section(string text) => OverviewPanel.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("GroupLabel") });
            void Hint(string text) => OverviewPanel.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Brush("Subtext"), Margin = new Thickness(0, 0, 0, 8) });
        }

        void RunSetupAgain()
        {
            Process.Start(AppSettings.InstalledExe, "--setup");
            Close();
        }

        // One WSL round trip: version of every installed tool (key=version) and the global agent
        // links (agent:name=target).
        static Dictionary<string, string> OverviewProbe(string distro)
        {
            const string script = "for t in claude codex opencode; do x=$(command -v $t || ls \"$HOME/.local/bin/$t\" \"$HOME/.$t/bin/$t\" 2>/dev/null | head -1); " +
                                  "[ -n \"$x\" ] && echo \"$t=$(\"$x\" --version 2>/dev/null | head -1)\"; done; " +
                                  "for f in \"$HOME\"/.claude/agents/*.md; do [ -e \"$f\" ] && echo \"agent:$(basename \"$f\" .md)=$(readlink -f \"$f\")\"; done; true";
            var result = new Dictionary<string, string>();
            foreach (var line in Shell.Capture("wsl.exe", $"-d {distro} -e bash -lc {Projects.WinArg(script)}").Output.Split('\n'))
            {
                var eq = line.IndexOf('=');
                if (eq > 0) result[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return result;
        }

        static string Shorten(string text) => text.Length > 180 ? text.Substring(0, 177) + " …" : text;
    }

    sealed class McpEditor
    {
        public readonly Border Root;
        public readonly StackPanel Actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        public readonly WrapPanel Footer = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        public readonly TextBlock Status = new TextBlock { FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        public readonly string OriginalName;
        readonly McpServer source;
        readonly TextBox name, url, header, scheme, command, args, env;
        readonly PasswordBox secret = new PasswordBox();
        readonly Dictionary<string, RadioButton> types = new Dictionary<string, RadioButton>();
        readonly StackPanel remote = new StackPanel(), local = new StackPanel();

        public McpEditor(MainWindow window, McpServer server, Action remove)
        {
            source = server;
            OriginalName = server.Name;
            T Res<T>(string key) => (T)window.FindResource(key);
            TextBox Box(string text) => new TextBox { Text = text ?? "" };
            FrameworkElement Field(string label, FrameworkElement input, Thickness margin)
            {
                var panel = new StackPanel { Margin = margin };
                panel.Children.Add(new TextBlock { Text = label, Style = Res<Style>("FieldLabel") });
                panel.Children.Add(input);
                return panel;
            }
            Grid Columns(params (FrameworkElement Element, GridLength Width)[] cells)
            {
                var grid = new Grid();
                for (int i = 0; i < cells.Length; i++)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = cells[i].Width });
                    Grid.SetColumn(cells[i].Element, i);
                    grid.Children.Add(cells[i].Element);
                }
                return grid;
            }

            name = Box(server.Name);
            url = Box(server.Url);
            header = Box(server.Header);
            scheme = Box(server.Scheme);
            command = Box(server.Command);
            args = Box(server.Args);
            env = new TextBox { Text = server.Env, AcceptsReturn = true, Height = 72, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            secret.Password = server.Secret;

            var group = Guid.NewGuid().ToString("N");
            var chips = new WrapPanel();
            foreach (var (key, label) in new[] { ("http", "HTTP"), ("sse", "SSE"), ("stdio", "Local (stdio)") })
            {
                var chip = new RadioButton { Style = Res<Style>("Chip"), Content = label, GroupName = group, IsChecked = server.Type == key };
                chip.Checked += (s, e) => ShowType(key);
                types[key] = chip;
                chips.Children.Add(chip);
            }

            var removeButton = new Button { Content = "Remove", Style = Res<Style>("Secondary"), Padding = new Thickness(12, 6, 12, 6),
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 0, 10) };
            removeButton.Click += (s, e) => remove();
            Actions.Children.Add(removeButton);

            var test = new Button { Content = "Test", Style = Res<Style>("Secondary"), Padding = new Thickness(14, 7, 14, 7) };
            var status = new TextBlock { FontSize = 12, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            test.Click += async (s, e) =>
            {
                test.IsEnabled = false;
                status.Foreground = Res<Brush>("Subtext");
                status.Text = "Checking the connection …";
                var (ok, message) = await MainWindow.ProbeMcp(Read());
                status.Foreground = ok ? Res<Brush>("Green") : Res<Brush>("Red");
                status.Text = message;
                test.IsEnabled = true;
            };

            remote.Children.Add(Field("URL", url, new Thickness(0, 0, 0, 10)));
            remote.Children.Add(Columns(
                (Field("Header", header, new Thickness(0, 0, 10, 10)), new GridLength(170)),
                (Field("Scheme", scheme, new Thickness(0, 0, 10, 10)), new GridLength(110)),
                (Field("Token (empty if no login is needed)", secret, new Thickness(0, 0, 0, 10)), new GridLength(1, GridUnitType.Star))));
            var testRow = new StackPanel { Orientation = Orientation.Horizontal };
            testRow.Children.Add(test);
            testRow.Children.Add(status);
            remote.Children.Add(testRow);

            local.Children.Add(Columns(
                (Field("Command", command, new Thickness(0, 0, 10, 10)), new GridLength(170)),
                (Field("Arguments", args, new Thickness(0, 0, 0, 10)), new GridLength(1, GridUnitType.Star))));
            local.Children.Add(Field("Environment variables, one per line as NAME=value", env, new Thickness(0)));

            var body = new StackPanel();
            body.Children.Add(Columns(
                (Field("Name", name, new Thickness(0, 0, 14, 10)), new GridLength(200)),
                (Field("Type", chips, new Thickness(0, 0, 0, 2)), new GridLength(1, GridUnitType.Star)),
                (Actions, GridLength.Auto)));
            body.Children.Add(remote);
            body.Children.Add(local);
            body.Children.Add(Footer);
            body.Children.Add(Status);
            Root = new Border { Background = Res<Brush>("Mantle"), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 16, 16),
                Margin = new Thickness(0, 0, 0, 10), Child = body };
            ShowType(server.Type);
        }

        void ShowType(string type)
        {
            remote.Visibility = type == "stdio" ? Visibility.Collapsed : Visibility.Visible;
            local.Visibility = type == "stdio" ? Visibility.Visible : Visibility.Collapsed;
        }

        string Type => types.First(t => t.Value.IsChecked == true).Key;

        public McpServer Read() => new McpServer
        {
            Name = name.Text.Trim(), Type = Type, Url = url.Text.Trim(), Header = header.Text.Trim(), Scheme = scheme.Text.Trim(),
            Secret = secret.Password, Command = command.Text.Trim(), Args = args.Text, Env = env.Text,
        };

        public string Validate()
        {
            var s = Read();
            var label = s.Name == "" ? "An MCP server" : $"MCP server {s.Name}";
            if (!Regex.IsMatch(s.Name, "^[A-Za-z0-9_.-]+$")) return $"{label}: the name is missing or contains spaces.";
            if (s.IsRemote && !Regex.IsMatch(s.Url, "^https?://")) return $"{label}: the URL must start with http:// or https://.";
            if (s.IsRemote && source.NeedsSecret && s.Secret == "") return $"{label}: the token is missing. Remove the server if you have none.";
            if (!s.IsRemote && s.Command == "") return $"{label}: the command is missing.";
            return null;
        }
    }
}
