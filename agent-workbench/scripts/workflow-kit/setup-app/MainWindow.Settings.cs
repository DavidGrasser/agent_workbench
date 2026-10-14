using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ClaudeWorkflowSetup
{
    // Every setting of Claude Code's settings.json, generated from the published schema, for the user
    // scope or for one project's settings.local.json.
    public partial class MainWindow
    {
        SettingsFile config;
        string configScope;
        ToolSettings configTool = ToolSettings.All[0];
        readonly List<FieldEditor> configEditors = new List<FieldEditor>();

        void ShowConfig(string scopePath) => ShowConfig(configTool, scopePath);

        async void ShowConfig(ToolSettings tool, string scopePath)
        {
            configTool = tool;
            ShowPage(Page.Config);
            MarkNav(Page.Config);
            var home = await Task.Run(() => Wsl.Home(appSettings.Distro));
            // AGENT_WORKBENCH_SETTINGS points Claude's user scope at another file, for testing against a copy.
            var userPath = tool.Key == "claude" ? Environment.GetEnvironmentVariable("AGENT_WORKBENCH_SETTINGS") ?? home + tool.RelativePath : home + tool.RelativePath;
            configScope = scopePath ?? userPath;
            ConfigLead.Text = scopePath == null
                ? $"{tool.Name} settings for every project: {configScope.Replace(home, "~")}. Changes apply to new sessions; a backup is written before saving."
                : $"Claude Code settings for this project only: {configScope}. Kept out of git through .git/info/exclude.";

            ConfigScope.Children.Clear();
            var selectedChips = new Dictionary<string, RadioButton>();
            void Chip(string label, bool selected, Action select, string group)
            {
                var chip = new RadioButton { Style = StyleOf("Chip"), Content = label, GroupName = group, IsChecked = selected };
                if (selected) selectedChips[group] = chip;
                chip.Checked += async (s, e) =>
                {
                    if (reverting) return;
                    if (await ConfirmLeave()) { select(); return; }
                    reverting = true;
                    if (selectedChips.TryGetValue(group, out var previous)) previous.IsChecked = true;
                    reverting = false;
                };
                ConfigScope.Children.Add(chip);
            }
            var installed = await InstalledTools();
            foreach (var t in ToolSettings.All.Where(t => installed.Count > 1 && (installed.Contains(t.Key) || t == tool)))
            {
                var target = t;
                Chip(t.Name, t == tool, () => ShowConfig(target, null), "configtool");
            }
            if (tool.Key == "claude")
            {
                if (installed.Count > 1) ConfigScope.Children.Add(new Border { Width = 1, Background = Brush("Surface1"), Margin = new Thickness(6, 4, 14, 12) });
                Chip("All projects", scopePath == null, () => ShowConfig(tool, null), "configscope");
                foreach (var project in Projects.FromVault(appSettings.VaultPath))
                    if (appSettings.ProjectPaths.TryGetValue(project.Name, out var root))
                    {
                        var path = root.TrimEnd('/') + "/.claude/settings.local.json";
                        Chip(project.Name, path == scopePath, () => ShowConfig(tool, path), "configscope");
                    }
            }

            ConfigPanel.Children.Clear();
            ConfigPanel.Children.Add(new TextBlock { Text = "Loading …", Foreground = Brush("Subtext") });
            try
            {
                config = await Task.Run(() => SettingsFile.Read(appSettings.Distro, configScope, tool.Toml));
            }
            catch (Exception ex)
            {
                ConfigPanel.Children.Clear();
                ConfigPanel.Children.Add(new TextBlock { Text = ex.Message, Foreground = Brush("Red") });
                return;
            }
            ConfigPanel.Children.Clear();
            configEditors.Clear();
            ConfigJump.Children.Clear();
            ConfigSearch.Text = "";

            var all = SettingsSchema.For(tool);
            var fields = all.Where(f => !f.Hidden).ToList();
            void Group(string title, IEnumerable<SettingField> group)
            {
                var list = group.ToList();
                if (list.Count == 0) return;
                var label = new TextBlock { Text = title, Style = StyleOf("GroupLabel") };
                ConfigPanel.Children.Add(label);
                var jump = new Button { Content = $"{title.Substring(0, 1)}{title.Substring(1).ToLowerInvariant()} ({list.Count})", Style = StyleOf("Secondary"),
                    Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 4), FontSize = 11.5 };
                jump.Click += (s, e) => ConfigScroll.ScrollToVerticalOffset(label.TransformToAncestor(ConfigPanel).Transform(new Point(0, 0)).Y);
                ConfigJump.Children.Add(jump);
                foreach (var field in list)
                {
                    var editor = new FieldEditor(this, field, config.Get(field.Key));
                    editor.Label = label;
                    configEditors.Add(editor);
                    ConfigPanel.Children.Add(editor.Root);
                }
            }
            Group("COMMON", tool.Common.Select(k => fields.FirstOrDefault(f => f.Key == k)).Where(f => f != null));
            if (tool.Key == "claude")
            {
                Group("PERMISSIONS", fields.Where(f => f.Key.StartsWith("permissions.")));
                Group("ENVIRONMENT", fields.Where(f => f.Key == "env"));
                Group("HOOKS AND STATUS LINE", fields.Where(f => f.Key == "hooks" || f.Key == "disableAllHooks" || f.Key == "statusLine" || f.Key == "subagentStatusLine"));
            }
            var shown = new HashSet<string>(configEditors.Select(e => e.Field.Key));
            Group("ALL OTHER SETTINGS", fields.Where(f => !shown.Contains(f.Key)).OrderBy(f => f.Key));
            // Keys the schema hides (deprecated, managed) still show up when the file actually sets them.
            var known = new HashSet<string>(all.Where(f => !f.Hidden || config.Get(f.Key) == null).Select(f => f.Key.Split('.')[0]));
            Group("OTHER KEYS IN THE FILE", config.Values.Keys.Where(k => !known.Contains(k) && !shown.Contains(k))
                .Select(k => all.FirstOrDefault(f => f.Key == k) ?? new SettingField { Key = k, Kind = FieldKind.Json,
                    Description = "Not in the published schema; kept as it is unless you change it here." }));
            Dispatcher.BeginInvoke(new Action(() => ConfigScroll.ScrollToTop()), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        void ConfigOnlySet_Click(object sender, RoutedEventArgs e) => ConfigSearch_TextChanged(null, null);

        void ConfigSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            var query = ConfigSearch.Text.Trim();
            bool onlySet = ConfigOnlySet.IsChecked == true;
            ConfigSearchHint.Visibility = ConfigSearch.Text == "" ? Visibility.Visible : Visibility.Collapsed;
            foreach (var editor in configEditors)
                editor.Root.Visibility = editor.Matches(query) && (!onlySet || editor.IsSet) ? Visibility.Visible : Visibility.Collapsed;
            foreach (var label in configEditors.Select(x => x.Label).Distinct())
                label.Visibility = configEditors.Any(x => x.Label == label && x.Root.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
        }

        async Task<bool> SaveConfig()
        {
            var set = new Dictionary<string, object>();
            var remove = new List<string>();
            foreach (var editor in configEditors)
            {
                var (changed, value, error) = editor.Read();
                if (error != null) { FooterNote.Text = $"{editor.Field.Key}: {error}"; return false; }
                if (!changed) continue;
                if (value == null) remove.Add(editor.Field.Key); else set[editor.Field.Key] = value;
            }
            if (set.Count + remove.Count == 0) { FooterNote.Text = "Nothing changed."; return true; }
            NextButton.IsEnabled = false;
            var (code, output) = await config.Save(set, remove, configScope.EndsWith("settings.local.json"));
            NextButton.IsEnabled = true;
            if (code != 0) { FooterNote.Text = "Saving failed: " + output; return false; }
            var scope = configScope.EndsWith("settings.local.json") ? configScope : null;
            // The edits are on disk now; the page may change without asking again.
            foreach (var editor in configEditors) editor.Accept();
            if (page == Page.Config) ShowConfig(configTool, scope);
            Notify($"\u2713 Saved {set.Count + remove.Count} change{(set.Count + remove.Count == 1 ? "" : "s")}. New {configTool.Name} sessions use the new values.");
            return true;
        }
    }

    sealed class FieldEditor
    {
        public readonly SettingField Field;
        public readonly Border Root;
        public TextBlock Label;
        readonly object original;
        readonly TextBlock title;
        readonly Dictionary<string, RadioButton> chips = new Dictionary<string, RadioButton>();
        readonly TextBox box;
        readonly MainWindow window;
        readonly FieldKind kind;

        public FieldEditor(MainWindow window, SettingField field, object value)
        {
            this.window = window;
            Field = field;
            original = value;
            // A choice whose current value is an object (Codex' granular approval policy, for example) is edited as JSON.
            kind = field.Kind == FieldKind.Choice && value != null && !(value is string || value is bool || value is int) ? FieldKind.Json : field.Kind;
            var panel = new StackPanel();
            title = new TextBlock { Text = field.Key, FontFamily = (System.Windows.Media.FontFamily)window.FindResource("Mono"), FontSize = 13, FontWeight = FontWeights.SemiBold };
            panel.Children.Add(title);
            var description = field.Description.Replace("\n", " ");
            // Two lines; "More" appears only when the text at the current width needs more.
            const double twoLines = 33;
            var text = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = window.BrushOf("Subtext"),
                Margin = new Thickness(0, 2, 0, 0), MaxHeight = twoLines };
            var more = new TextBlock { Text = "More", FontSize = 11.5, Foreground = window.BrushOf("Blue"), Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
            bool expanded = false;
            more.MouseLeftButtonUp += (s, e) =>
            {
                expanded = !expanded;
                text.MaxHeight = expanded ? double.PositiveInfinity : twoLines;
                more.Text = expanded ? "Less" : "More";
            };
            text.SizeChanged += (s, e) =>
            {
                if (expanded || text.ActualWidth <= 0) return;
                var probe = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, FontSize = 12, FontFamily = text.FontFamily };
                probe.Measure(new Size(text.ActualWidth, double.PositiveInfinity));
                more.Visibility = probe.DesiredSize.Height > twoLines + 1 ? Visibility.Visible : Visibility.Collapsed;
            };
            panel.Children.Add(text);
            panel.Children.Add(more);
            panel.Children.Add(new Border { Height = 8 });

            if (kind == FieldKind.Boolean || kind == FieldKind.Choice)
            {
                var wrap = new WrapPanel();
                var options = new List<(string Key, string Label)> { ("", "Default") };
                options.AddRange(kind == FieldKind.Boolean ? new[] { ("true", "On"), ("false", "Off") } : field.Choices.Select(c => (c, c)));
                var current = value == null ? "" : value is bool b ? (b ? "true" : "false") : value.ToString();
                if (current != "" && options.All(o => o.Key != current)) options.Add((current, current));
                var group = Guid.NewGuid().ToString("N");
                foreach (var (key, label) in options)
                {
                    var chip = new RadioButton { Style = window.StyleOf("Chip"), Content = label, GroupName = group, IsChecked = key == current };
                    chip.Checked += (s, e) => Mark();
                    chips[key] = chip;
                    wrap.Children.Add(chip);
                }
                panel.Children.Add(wrap);
            }
            else
            {
                bool multiline = kind == FieldKind.List || kind == FieldKind.Env || kind == FieldKind.Json;
                box = new TextBox { Text = Show(value), AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap,
                    MinHeight = multiline ? 60 : 0, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                if (kind == FieldKind.Json) { box.FontFamily = (System.Windows.Media.FontFamily)window.FindResource("Mono"); box.FontSize = 12; }
                box.TextChanged += (s, e) => Mark();
                panel.Children.Add(box);
                var hint = kind == FieldKind.List ? "One entry per line." : kind == FieldKind.Env ? "One variable per line as NAME=value."
                    : kind == FieldKind.Json ? "JSON. Empty removes the setting." : field.Choices.Count > 0 ? "For example: " + string.Join(", ", field.Choices) : null;
                if (hint != null) panel.Children.Add(new TextBlock { Text = hint, FontSize = 11, Foreground = window.BrushOf("Overlay0"), Margin = new Thickness(0, 4, 0, 0) });
            }
            Root = new Border { Background = window.BrushOf("Mantle"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 12),
                Margin = new Thickness(0, 0, 0, 8), Child = panel };
        }

        string Show(object value)
        {
            if (value == null) return "";
            switch (kind)
            {
                case FieldKind.List: return value is System.Collections.IEnumerable list && !(value is string) ? string.Join("\n", list.Cast<object>()) : value.ToString();
                case FieldKind.Env: return value is Dictionary<string, object> env ? string.Join("\n", env.Select(kv => $"{kv.Key}={kv.Value}")) : "";
                case FieldKind.Json: return Pretty(SettingsFile.ToJson(value));
                case FieldKind.Number: return Convert.ToDouble(value).ToString(CultureInfo.InvariantCulture);
                default: return value.ToString();
            }
        }

        // JavaScriptSerializer writes compact JSON; indent it for editing.
        static string Pretty(string json)
        {
            var result = new System.Text.StringBuilder();
            int depth = 0;
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\')) inString = !inString;
                if (inString) { result.Append(c); continue; }
                if ((c == '{' || c == '[') && i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) { result.Append(c).Append(json[++i]); }
                else if (c == '{' || c == '[') { result.Append(c).Append('\n').Append(' ', ++depth * 2); }
                else if (c == '}' || c == ']') { result.Append('\n').Append(' ', --depth * 2).Append(c); }
                else if (c == ',') { result.Append(",\n").Append(' ', depth * 2); }
                else if (c == ':') { result.Append(": "); }
                else result.Append(c);
            }
            return result.ToString();
        }

        bool accepted;

        // After a save the page reloads; until then the editor no longer counts as changed.
        public void Accept() => accepted = true;

        void Mark()
        {
            var (changed, _, error) = Read();
            title.Foreground = error != null ? window.BrushOf("Red") : changed ? window.BrushOf("Blue") : window.BrushOf("Text");
        }

        public bool IsSet => original != null;

        public bool Matches(string query) =>
            query == "" || Field.Key.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || Field.Description.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        // Returns whether the value changed and the new value; null means the key is removed.
        public (bool Changed, object Value, string Error) Read()
        {
            if (accepted) return (false, null, null);
            if (box == null)
            {
                var key = chips.First(c => c.Value.IsChecked == true).Key;
                object value = key == "" ? null : kind == FieldKind.Boolean ? (object)(key == "true") : key;
                var before = original == null ? "" : original is bool b ? (b ? "true" : "false") : original.ToString();
                return (key != before, value, null);
            }
            var text = box.Text.Replace("\r", "");
            if (text.Trim() == "") return (original != null, null, null);
            object parsed;
            switch (kind)
            {
                case FieldKind.Integer:
                    if (!int.TryParse(text.Trim(), out var n)) return (true, null, "a whole number is expected");
                    parsed = n; break;
                case FieldKind.Number:
                    if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return (true, null, "a number is expected");
                    parsed = d; break;
                case FieldKind.List:
                    parsed = text.Split('\n').Select(l => l.Trim()).Where(l => l != "").ToList(); break;
                case FieldKind.Env:
                    var env = new Dictionary<string, object>();
                    foreach (var line in text.Split('\n').Select(l => l.Trim()).Where(l => l != ""))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) return (true, null, $"'{line}' is not NAME=value");
                        env[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                    parsed = env; break;
                case FieldKind.Json:
                    try { parsed = SettingsFile.FromJson(text); }
                    catch (Exception ex) { return (true, null, "invalid JSON: " + ex.Message.Split('.')[0]); }
                    break;
                default:
                    parsed = text.Trim(); break;
            }
            bool changed = original == null || SettingsFile.ToJson(parsed) != SettingsFile.ToJson(Normalize(original));
            return (changed, parsed, null);
        }

        // The original as the editor would produce it, so an untouched field compares equal.
        object Normalize(object value)
        {
            switch (kind)
            {
                case FieldKind.List: return value is System.Collections.IEnumerable list && !(value is string) ? list.Cast<object>().Select(x => x.ToString()).ToList() : value;
                case FieldKind.Env: return value is Dictionary<string, object> env ? env.ToDictionary(kv => kv.Key, kv => (object)(kv.Value?.ToString() ?? "")) : value;
                case FieldKind.Number: return Convert.ToDouble(value);
                case FieldKind.Integer: return Convert.ToInt32(value);
                case FieldKind.Text: return value.ToString();
                default: return value;
            }
        }
    }
}
