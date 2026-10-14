using System;
using System.Linq;
using System.Windows;

namespace ClaudeWorkflowSetup
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            var self = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            bool installed = string.Equals(self, AppSettings.InstalledExe, StringComparison.OrdinalIgnoreCase);
            bool manage = (installed || e.Args.Contains("--manage")) && !e.Args.Contains("--setup") && AppSettings.Load() != null;
            new MainWindow(e.Args.Contains("--resume"), manage).Show();
        }

        // WPF scrolls 48 px per wheel notch (delta 120); this halves it.
        const double PixelsPerDelta = 0.2;

        void ScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            var viewer = (System.Windows.Controls.ScrollViewer)sender;
            // The event tunnels from the outermost viewer inwards; the innermost one that can still
            // move in this direction takes it.
            if (Innermost(e.OriginalSource as DependencyObject, viewer, e.Delta) != viewer) return;
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset - e.Delta * PixelsPerDelta);
            e.Handled = true;
        }

        static System.Windows.Controls.ScrollViewer Innermost(DependencyObject source, System.Windows.Controls.ScrollViewer outer, int delta)
        {
            for (var node = source; node != null && node != outer; node = Parent(node))
                if (node is System.Windows.Controls.ScrollViewer inner && CanScroll(inner, delta)) return inner;
            return CanScroll(outer, delta) ? outer : null;
        }

        // VisualTreeHelper throws for text elements such as a Run.
        static DependencyObject Parent(DependencyObject node) =>
            node is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
                                                : LogicalTreeHelper.GetParent(node);

        static bool CanScroll(System.Windows.Controls.ScrollViewer viewer, int delta) =>
            delta > 0 ? viewer.VerticalOffset > 0 : viewer.VerticalOffset < viewer.ScrollableHeight;
    }
}
