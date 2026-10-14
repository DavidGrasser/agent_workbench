using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ClaudeWorkflowSetup
{
    // Projects: which agents, MCP servers and instructions apply in a folder, and the personal agent
    // sets that are linked into it without ever entering the repository.
    public partial class MainWindow
    {
        string VaultLinux => Wsl.ToLinux(appSettings.Distro, appSettings.VaultPath);

        async void ShowProjects()
        {
            ShowPage(Page.Projects);
            MarkNav(Page.Projects);
            ProjectsTitle.Text = "Projects";
            ProjectsCrumb.Visibility = Visibility.Collapsed;
            ProjectsLead.Text = "One project per repository. Each project has a note in the vault's projects/ folder and its own agent set; " +
                                "repositories inside it are part of it.";
            ProjectsPanel.Children.Clear();

            var projects = Projects.FromVault(appSettings.VaultPath);
            if (projects.Count == 0) Hint("No projects yet. A project is a repository: create one to give it a vault note and its own agents.");
            ProjectsPanel.Children.Add(ProjectTiles(projects, true));
            Dispatcher.BeginInvoke(new Action(() => ProjectsScroll.ScrollToTop()), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // One tile per project, the whole tile opens it; a project not cloned on this PC asks for its folder.
        WrapPanel ProjectTiles(List<VaultProject> projects, bool withNew)
        {
            var tiles = new WrapPanel();
            var sets = Projects.Sets(appSettings.VaultPath);
            foreach (var project in projects)
            {
                var local = appSettings.ProjectPaths.TryGetValue(project.Name, out var path) ? path : null;
                var agents = sets.TryGetValue(project.Name, out var list) ? list.Count : 0;
                var body = (local ?? "Not on this PC yet: click to locate the cloned folder.") +
                           (agents > 0 ? $"\n{agents} personal agent{(agents == 1 ? "" : "s")}" : "");
                tiles.Children.Add(Tile(project.Name, project.Remote, body,
                    local != null ? (Action)(() => ShowProject(local)) : () => LocateProject(project), 320));
            }
            if (withNew) tiles.Children.Add(Tile("+ New project", "From a repository on this PC", "Creates the vault note and an agent set.", ShowNewProject, 320));
            return tiles;
        }

        void LocateProject(VaultProject project)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = $"Where is {project.Name} on this PC?" })
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                var (root, remote) = Projects.RepoOf(appSettings.Distro, dialog.SelectedPath);
                if (root == null) { MessageBox.Show(this, "This folder is not inside a git repository.", "Agent Workbench"); return; }
                if (!SameRemote(remote, project.Remote) && MessageBox.Show(this,
                        $"The repository's remote is\n{remote}\nbut the project expects\n{project.Remote}\n\nUse it anyway?", "Agent Workbench",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                appSettings.ProjectPaths[project.Name] = root;
                appSettings.Save();
                ShowProject(root);
            }
        }

        static bool SameRemote(string a, string b)
        {
            string Key(string r) => System.Text.RegularExpressions.Regex.Replace((r ?? "").Trim().ToLowerInvariant(), "(\\.git)?/?$", "")
                .Replace("git@github.com:", "github.com/").Replace("https://", "").Replace("ssh://git@", "");
            return Key(a) == Key(b);
        }

        async void ShowNewProject()
        {
            ShowPage(Page.Project);
            MarkNav(Page.Projects);
            ProjectsTitle.Text = "New project";
            ProjectsCrumb.Visibility = Visibility.Visible;
            Crumb(ProjectsCrumb, ("Projects", ShowProjects), ("New project", null));
            ProjectsLead.Text = "Pick the repository. The project gets a note in projects/ and an agent set in project-agents/.";
            ProjectsPanel.Children.Clear();
            ProjectsPanel.Children.Add(new TextBlock { Text = "Looking for repositories Claude has been used in …", Foreground = Brush("Subtext") });

            var taken = new HashSet<string>(appSettings.ProjectPaths.Values);
            var suggestions = await Task.Run(() => Projects.SuggestRepos(appSettings.Distro, Projects.Known(appSettings.Distro, appSettings.ExtraProjects)));
            ProjectsPanel.Children.Clear();

            Section("REPOSITORY");
            string chosenRoot = null, chosenRemote = null;
            var name = new TextBox();
            var picked = new TextBlock { FontSize = 12, Foreground = Brush("Subtext"), Margin = new Thickness(0, 6, 0, 0) };
            var chips = new WrapPanel();
            void Pick(string root, string remote)
            {
                chosenRoot = root;
                chosenRemote = remote;
                picked.Text = $"{root}\n{(remote == "" ? "no remote named origin" : remote)}";
                name.Text = Path.GetFileName(root.TrimEnd('/'));
            }
            foreach (var (root, remote) in suggestions.Where(r => !taken.Contains(r.Root)))
            {
                var chip = new RadioButton { Style = StyleOf("Chip"), Content = Path.GetFileName(root.TrimEnd('/')), GroupName = "newproject", ToolTip = root };
                chip.Checked += (s, e) => Pick(root, remote);
                chips.Children.Add(chip);
            }
            if (chips.Children.Count == 0) Hint("No unregistered repositories found among the folders Claude was started in.");
            ProjectsPanel.Children.Add(chips);
            var browse = new Button { Content = "Choose another folder", Style = StyleOf("Secondary"), HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 4, 0, 0) };
            browse.Click += (s, e) =>
            {
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose a folder inside the repository" })
                {
                    if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                    var (root, remote) = Projects.RepoOf(appSettings.Distro, dialog.SelectedPath);
                    if (root == null) { FooterNote.Text = "That folder is not inside a git repository."; return; }
                    foreach (RadioButton chip in chips.Children) chip.IsChecked = false;
                    Pick(root, remote);
                }
            };
            ProjectsPanel.Children.Add(browse);
            ProjectsPanel.Children.Add(picked);

            Section("NAME");
            Hint("Becomes the note projects/<name>.md and the agent set project-agents/<name>/.");
            ProjectsPanel.Children.Add(name);

            var create = new Button { Content = "Create project", Style = StyleOf("Primary"), HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 16, 0, 0) };
            create.Click += (s, e) =>
            {
                var n = name.Text.Trim();
                if (chosenRoot == null) { FooterNote.Text = "Pick a repository first."; return; }
                if (!System.Text.RegularExpressions.Regex.IsMatch(n, "^[A-Za-z0-9._-]+$")) { FooterNote.Text = "Name: letters, digits, dots, dashes and underscores."; return; }
                var existing = Projects.FromVault(appSettings.VaultPath).FirstOrDefault(p => p.Name == n);
                if (existing == null && File.Exists(Path.Combine(appSettings.VaultPath, "projects", n + ".md")))
                { FooterNote.Text = $"projects/{n}.md exists as another kind of note. Choose a different name."; return; }
                if (existing != null && !SameRemote(existing.Remote, chosenRemote))
                { FooterNote.Text = $"A project named {n} already exists for {existing.Remote}."; return; }
                if (existing == null) Projects.CreateNote(appSettings.VaultPath, n, chosenRemote);
                appSettings.ProjectPaths[n] = chosenRoot;
                appSettings.Save();
                ShowProject(chosenRoot);
            };
            ProjectsPanel.Children.Add(create);
            Dispatcher.BeginInvoke(new Action(() => ProjectsScroll.ScrollToTop()), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        async void ShowProject(string path)
        {
            ShowPage(Page.Project);
            MarkNav(Page.Projects);
            var project = Projects.FromVault(appSettings.VaultPath)
                .FirstOrDefault(p => appSettings.ProjectPaths.TryGetValue(p.Name, out var local) && local == path);
            ProjectsTitle.Text = project?.Name ?? Path.GetFileName(path.TrimEnd('/'));
            ProjectsCrumb.Visibility = Visibility.Visible;
            Crumb(ProjectsCrumb, ("Projects", ShowProjects), (ProjectsTitle.Text, null));
            ProjectsLead.Text = path;
            ProjectsPanel.Children.Clear();
            ProjectsPanel.Children.Add(new TextBlock { Text = "Reading the project …", Foreground = Brush("Subtext") });
            ProjectsScroll.ScrollToTop();

            var vault = VaultLinux;
            var info = await Task.Run(() => Projects.Inspect(appSettings.Distro, path, vault));
            var nested = await Task.Run(() => Projects.NestedRepos(appSettings.Distro, info.Root));
            var global = VaultInfo.Scan(appSettings.VaultPath).Agents;
            var userMcp = await Task.Run(() => Wsl.ConfiguredMcpServers(appSettings.Distro));
            var sets = Projects.Sets(appSettings.VaultPath);
            var globalSets = await Task.Run(() => Projects.GlobalSets(appSettings.Distro));
            ProjectsPanel.Children.Clear();
            if (info.Root != path) ProjectsLead.Text = $"{path}\nRepository: {info.Root}";

            if (project != null)
            {
                var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                var note = new Button { Content = "Open note", Style = StyleOf("Secondary"), Padding = new Thickness(14, 6, 14, 6) };
                note.Click += (s, e) => Installer.OpenObsidian(project.NoteFile);
                var folder = new Button { Content = "Open folder", Style = StyleOf("Secondary"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
                folder.Click += (s, e) => Process.Start("explorer.exe", $"\"{Projects.WindowsPath(appSettings.Distro, path)}\"");
                links.Children.Add(note);
                links.Children.Add(folder);
                ProjectsPanel.Children.Add(links);
            }

            // Repositories
            Section($"REPOSITORIES ({1 + nested.Count})");
            Row(Path.GetFileName(info.Root.TrimEnd('/')), info.Root, "root");
            foreach (var repo in nested) Row(Path.GetFileName(repo), repo, "inside");

            // Agents
            Section($"AGENTS ({info.Agents.Count + global.Count})");
            foreach (var agent in info.Agents)
            {
                var where = agent.IsPersonal ? $"personal, linked from {RelativeToVault(agent.LinkTarget, vault)}"
                    : agent.LinkTarget != "" ? $"linked from {agent.LinkTarget}" : $"in the project, {agent.Folder}/.claude/agents";
                Row(agent.Name, where, agent.IsPersonal ? "personal" : "project");
            }
            foreach (var agent in global.Where(g => info.Agents.All(a => a.Name != g.Name)))
                Row(agent.Name, "global, from the vault's agents folder", "global");
            if (info.Agents.Count + global.Count == 0) Hint("No agents apply here.");

            // Personal agent sets
            Section("PERSONAL AGENT SETS");
            Hint("Sets live in the vault under project-agents/. Linking puts symlinks into .claude/agents and lists them in " +
                 ".git/info/exclude, so they never show up in git and never reach the repository.");
            CheckBox withNested = null;
            if (nested.Count > 0)
            {
                withNested = new CheckBox { Style = StyleOf("ChipToggle"), IsChecked = true, Margin = new Thickness(0, 0, 0, 10),
                    Content = $"Also link into the {nested.Count} nested repositories ({string.Join(", ", nested.Select(n => Path.GetFileName(n)))})" };
                ProjectsPanel.Children.Add(withNested);
            }
            var targets = new Func<List<string>>(() => new[] { info.Root }.Concat(withNested?.IsChecked == true ? nested : new List<string>()).ToList());
            foreach (var set in sets.OrderBy(x => project != null && x.Key == project.Name ? 0 : 1))
            {
                int linked = set.Value.Count(name => info.Agents.Any(a => a.IsPersonal && a.Name == name && a.LinkTarget.Contains($"/{Projects.PersonalFolder}/{set.Key}/")));
                var card = SetCard(set.Key, set.Value, linked, targets, path, globalSets.Contains(set.Key));
                ProjectsPanel.Children.Add(card);
            }
            if (global.Count > 0) ProjectsPanel.Children.Add(MoveGlobalCard(global, Path.GetFileName(info.Root.TrimEnd('/')), sets, targets, path));

            // MCP servers
            Section("MCP SERVERS");
            foreach (var server in userMcp) Row(server.Name, server.Summary, "global");
            foreach (var server in info.LocalMcp) Row(server.Name, server.Summary, "this folder");
            foreach (var name in info.ProjectMcp) Row(name, "from .mcp.json in the repository", "project");
            if (userMcp.Count + info.LocalMcp.Count + info.ProjectMcp.Count == 0) Hint("No MCP servers apply here.");

            // Instructions
            Section("INSTRUCTIONS (CLAUDE.md)");
            foreach (var file in info.Instructions)
            {
                var real = info.RealPaths.TryGetValue(file, out var r) ? r : file;
                var windows = Projects.WindowsPath(appSettings.Distro, real);
                AddAction(ProjectsPanel, Path.GetFileName(file), real == file ? file : $"{file}\n\u2192 {windows}", "Open",
                    () => Process.Start(new ProcessStartInfo("notepad.exe", $"\"{windows}\"") { UseShellExecute = true }));
            }
            if (info.Instructions.Count == 0) Hint("No CLAUDE.md applies here.");
            // After the new content has been laid out, otherwise the old offset survives.
            Dispatcher.BeginInvoke(new Action(() => ProjectsScroll.ScrollToTop()), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        Border SetCard(string set, List<string> agents, int linked, Func<List<string>> targets, string projectPath, bool isGlobal)
        {
            var card = new Border { Background = Brush("Surface0"), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 12, 12), Margin = new Thickness(0, 0, 0, 8) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            text.Children.Add(new TextBlock { Text = set, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            text.Children.Add(new TextBlock { FontSize = 12, Foreground = Brush("Subtext"), Margin = new Thickness(0, 2, 0, 0),
                Text = $"{linked} of {agents.Count} linked here · {string.Join(", ", agents)}" });
            var everywhere = new CheckBox { Style = StyleOf("ChipToggle"), IsChecked = isGlobal, Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, Content = "Available everywhere on this PC",
                ToolTip = "Lists the set in ~/.claude/global-agent-sets. Only this PC is affected, the vault does not change." };
            text.Children.Add(everywhere);
            var status = new TextBlock { FontSize = 12, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
            text.Children.Add(status);
            grid.Children.Add(text);
            everywhere.Click += async (s, e) =>
            {
                everywhere.IsEnabled = false;
                var (code, output) = await Projects.SetGlobal(appSettings.Distro, set, everywhere.IsChecked == true);
                everywhere.IsEnabled = true;
                status.Visibility = Visibility.Visible;
                status.Foreground = code == 0 ? Brush("Green") : Brush("Red");
                status.Text = code == 0
                    ? (everywhere.IsChecked == true ? "\u2713 Global on this PC from the next Claude start." : "\u2713 Only in linked projects from the next Claude start.")
                    : "Failed: " + output;
            };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var link = new Button { Content = linked == agents.Count ? "Relink" : "Link all", Style = StyleOf("Primary"), Padding = new Thickness(14, 6, 14, 6) };
            var unlink = new Button { Content = "Unlink all", Style = StyleOf("Secondary"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = linked > 0 };
            link.Click += async (s, e) => await ApplySet(set, agents, targets(), true, status, projectPath);
            unlink.Click += async (s, e) => await ApplySet(set, agents, targets(), false, status, projectPath);
            buttons.Children.Add(link);
            buttons.Children.Add(unlink);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);
            card.Child = grid;
            return card;
        }

        async Task ApplySet(string set, List<string> agents, List<string> roots, bool linkIt, TextBlock status, string projectPath)
        {
            status.Visibility = Visibility.Visible;
            status.Foreground = Brush("Subtext");
            status.Text = linkIt ? "Linking …" : "Unlinking …";
            var vault = VaultLinux;
            var errors = new List<string>();
            foreach (var root in roots)
                foreach (var agent in agents)
                {
                    var (code, output) = linkIt
                        ? await Projects.Link(appSettings.Distro, root, $"{vault}/{Projects.PersonalFolder}/{set}/{agent}.md")
                        : await Projects.Unlink(appSettings.Distro, root, agent);
                    if (code != 0 && !(output.StartsWith("not a link") && !linkIt)) errors.Add($"{Path.GetFileName(root)}/{agent}: {output}");
                }
            if (errors.Count > 0)
            {
                status.Foreground = Brush("Red");
                status.Text = string.Join("\n", errors.Take(5));
                return;
            }
            ShowProject(projectPath);
        }

        Border MoveGlobalCard(List<AgentInfo> global, string defaultSet, Dictionary<string, List<string>> sets, Func<List<string>> targets, string projectPath)
        {
            var card = new Border { Background = Brush("Mantle"), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 16, 14), Margin = new Thickness(0, 4, 0, 8) };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "Make global agents personal to this project", FontWeight = FontWeights.SemiBold, FontSize = 14 });
            panel.Children.Add(new TextBlock { FontSize = 12, Foreground = Brush("Subtext"), Margin = new Thickness(0, 2, 0, 8),
                Text = "Moves the selected agents from the vault's agents folder into a set and links it here. They stop being available everywhere else." });
            var chips = new WrapPanel();
            var picks = global.Select(a => new CheckBox { Style = StyleOf("ChipToggle"), Content = a.Name, Tag = a }).ToList();
            foreach (var chip in picks) chips.Children.Add(chip);
            panel.Children.Add(chips);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            row.Children.Add(new TextBlock { Text = "Set", FontSize = 12, Foreground = Brush("Subtext"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            var setName = new TextBox { Text = sets.ContainsKey(defaultSet) || sets.Count == 0 ? defaultSet : sets.Keys.First(), Width = 200 };
            row.Children.Add(setName);
            var move = new Button { Content = "Move and link", Style = StyleOf("Primary"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(10, 0, 0, 0) };
            row.Children.Add(move);
            panel.Children.Add(row);
            var status = new TextBlock { FontSize = 12, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
            panel.Children.Add(status);

            move.Click += async (s, e) =>
            {
                var chosen = picks.Where(c => c.IsChecked == true).Select(c => (AgentInfo)c.Tag).ToList();
                var name = setName.Text.Trim();
                status.Visibility = Visibility.Visible;
                status.Foreground = Brush("Red");
                if (chosen.Count == 0) { status.Text = "Select at least one agent."; return; }
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9._-]+$")) { status.Text = "Set name: letters, digits, dots, dashes and underscores."; return; }
                var folder = Path.Combine(appSettings.VaultPath, Projects.PersonalFolder, name);
                Directory.CreateDirectory(folder);
                foreach (var agent in chosen)
                {
                    var target = Path.Combine(folder, Path.GetFileName(agent.File));
                    if (File.Exists(target)) { status.Text = $"{Path.GetFileName(agent.File)} already exists in the set."; return; }
                    File.Move(agent.File, target);
                }
                await ApplySet(name, chosen.Select(a => Path.GetFileNameWithoutExtension(a.File)).ToList(), targets(), true, status, projectPath);
            };
            card.Child = panel;
            return card;
        }

        static string RelativeToVault(string target, string vault) =>
            target.StartsWith(vault.TrimEnd('/') + "/") ? target.Substring(vault.TrimEnd('/').Length + 1) : target;

        void Section(string text) => ProjectsPanel.Children.Add(new TextBlock { Text = text, Style = StyleOf("GroupLabel") });

        void Hint(string text) => ProjectsPanel.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Brush("Subtext"), Margin = new Thickness(0, 0, 0, 8) });

        void Row(string title, string detail, string badge)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = Brush("Subtext") });
            grid.Children.Add(text);
            var colors = new Dictionary<string, string> { ["personal"] = "Mauve", ["project"] = "Blue", ["global"] = "Surface1", ["this folder"] = "Yellow",
                ["root"] = "Green", ["inside"] = "Surface1" };
            var tag = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 2, 9, 3), VerticalAlignment = VerticalAlignment.Top,
                Background = Brush(colors.TryGetValue(badge, out var c) ? c : "Surface1"),
                Child = new TextBlock { Text = badge, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = badge == "global" || badge == "inside" ? Brush("Subtext") : Brush("Crust") } };
            Grid.SetColumn(tag, 1);
            grid.Children.Add(tag);
            ProjectsPanel.Children.Add(new Border { Background = Brush("Mantle"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6), Child = grid });
        }
    }
}
