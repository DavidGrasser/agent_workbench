using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ClaudeWorkflowSetup
{
    // Building blocks of the management pages: clickable tiles, the path above a detail page,
    // and the guard that keeps unsaved edits from getting lost.
    public partial class MainWindow
    {
        // MCP editors on screen with a way to tell whether they changed and to save them.
        readonly Dictionary<McpEditor, (string Original, Func<Task<bool>> Save)> mcpEdits = new Dictionary<McpEditor, (string, Func<Task<bool>>)>();
        string agentSnapshot;
        bool leaving;

        Button Tile(string title, string meta, string body, Action open, double width = double.NaN)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(meta))
                text.Children.Add(new TextBlock { Text = meta, FontSize = 12, Foreground = Brush("Subtext"), Margin = new Thickness(0, 2, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(body))
                text.Children.Add(new TextBlock { Text = body, FontSize = 12, Foreground = Brush("Subtext"), Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap });
            var tile = new Button { Style = StyleOf("Tile"), Content = text, Width = width, ToolTip = title };
            if (!double.IsNaN(width)) tile.Margin = new Thickness(0, 0, 10, 10);
            System.Windows.Automation.AutomationProperties.SetName(tile, title);
            tile.Click += (s, e) => open();
            return tile;
        }

        // A card with several actions on the right, such as the vault's "Open in Obsidian" and "Open folder".
        void ActionCard(Panel target, string title, string description, params (string Label, Action Run)[] actions)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = Brush("Subtext"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
            grid.Children.Add(text);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var (label, run) in actions)
            {
                var b = new Button { Content = label, Style = StyleOf("Secondary"), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(8, 0, 0, 0) };
                b.Click += (s, e) => run();
                buttons.Children.Add(b);
            }
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);
            target.Children.Add(new Border { Background = Brush("Surface0"), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 12, 12),
                Margin = new Thickness(0, 0, 0, 8), Child = grid });
        }

        // "Projects › my-project": every part but the last goes back.
        void Crumb(Panel target, params (string Label, Action Go)[] parts)
        {
            target.Children.Clear();
            for (int i = 0; i < parts.Length; i++)
            {
                var (label, go) = parts[i];
                var text = new TextBlock { Text = label, FontSize = 12.5, Margin = new Thickness(0, 0, 6, 4) };
                if (go != null && i < parts.Length - 1)
                {
                    text.Foreground = Brush("Blue");
                    text.Cursor = Cursors.Hand;
                    text.MouseLeftButtonUp += (s, e) => go();
                }
                else text.Foreground = Brush("Subtext");
                target.Children.Add(text);
                if (i < parts.Length - 1) target.Children.Add(new TextBlock { Text = "›", FontSize = 12.5, Foreground = Brush("Overlay0"), Margin = new Thickness(0, 0, 6, 4) });
            }
        }

        // A success message in the footer that clears itself.
        async void Notify(string text)
        {
            FooterNote.Foreground = Brush("Green");
            FooterNote.Text = text;
            await Task.Delay(4000);
            if (FooterNote.Text == text) FooterNote.Text = "";
            FooterNote.Foreground = Brush("Red");
        }

        static string Snapshot(McpServer s) =>
            string.Join("\u0001", s.Name, s.Type, s.Url, s.Header, s.Scheme, s.Secret, s.Command, s.Args, s.Env);

        static string Snapshot(AgentForm f) =>
            string.Join("\u0001", f.AgentName, f.Description, f.Model, f.Tools == null ? "*" : string.Join(",", f.Tools), f.Instructions);

        bool PageDirty()
        {
            switch (page)
            {
                case Page.Config: return configEditors.Any(e => e.Read().Changed);
                case Page.AgentEdit: return agentForm != null && agentSnapshot != null && Snapshot(agentForm) != agentSnapshot;
                case Page.McpManage: return mcpEdits.Any(kv => Snapshot(kv.Key.Read()) != kv.Value.Original);
                default: return false;
            }
        }

        async Task<bool> SaveCurrentPage()
        {
            switch (page)
            {
                case Page.Config: return await SaveConfig();
                case Page.AgentEdit: return await SaveAgent();
                case Page.McpManage:
                    foreach (var kv in mcpEdits.Where(kv => Snapshot(kv.Key.Read()) != kv.Value.Original).ToList())
                        if (!await kv.Value.Save()) return false;
                    return true;
                default: return true;
            }
        }

        // Asks before unsaved edits are dropped. True means the caller may go on.
        async Task<bool> ConfirmLeave()
        {
            if (!PageDirty()) return true;
            var answer = MessageBox.Show(this, "This page has unsaved changes. Save them before you leave?", "Agent Workbench",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.No) return true;
            return await SaveCurrentPage();
        }

        async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.S || Keyboard.Modifiers != ModifierKeys.Control) return;
            if (page != Page.Config && page != Page.AgentEdit && page != Page.McpManage) return;
            e.Handled = true;
            if (!PageDirty()) { FooterNote.Text = "Nothing to save."; return; }
            await SaveCurrentPage();
        }

        async void Window_Closing(object sender, CancelEventArgs e)
        {
            if (leaving || !PageDirty()) return;
            e.Cancel = true;
            if (!await ConfirmLeave()) return;
            leaving = true;
            Close();
        }
    }
}
