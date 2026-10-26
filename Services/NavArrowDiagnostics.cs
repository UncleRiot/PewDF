using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PewDF.Services
{
    // SEMANTIC ANCHOR: NAV_ARROW_DIAGNOSTICS_SSOT
    // Read-only, opt-in measurements of the actual WPF controls after layout.
    // Logging must not mutate layout, styles, navigation, or user settings.
    internal static class NavArrowDiagnostics
    {
        internal static void Observe(Button button, string label)
        {
            void RecordAfterLayout()
            {
                if (!DebugLogging.Enabled || !button.IsVisible) return;
                button.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    if (!DebugLogging.Enabled || !button.IsLoaded || !button.IsVisible) return;
                    var content = button.Content as FrameworkElement;
                    var presenter = FindDescendant<ContentPresenter>(button);
                    var textBlock = content as TextBlock ?? FindDescendant<TextBlock>(presenter);
                    var transform = textBlock?.RenderTransform ?? content?.RenderTransform;
                    string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
                    string Rect(FrameworkElement? e) => e is null ? "none" :
                        $"{Format(e.ActualWidth)}x{Format(e.ActualHeight)}";
                    string Transform(Transform? t) => t is null ? "none" :
                        $"{t.GetType().Name}({t.Value})";
                    var text = textBlock?.Text ?? button.Content as string ?? "";
                    var codepoints = string.Join(",", text.Select(ch => $"U+{(int)ch:X4}"));
                    DebugLogging.Write("nav-arrow-layout.log",
                        $"NAV_ARROW {label} glyph={codepoints} button={Rect(button)} " +
                        $"buttonFont={button.FontFamily?.Source}/{Format(button.FontSize)} " +
                        $"buttonPadding={button.Padding} contentType={button.Content?.GetType().Name ?? "null"} " +
                        $"content={Rect(content)} presenter={Rect(presenter)} " +
                        $"presenterAlignment={presenter?.HorizontalAlignment}/{presenter?.VerticalAlignment} " +
                        $"text={Rect(textBlock)} textFont={textBlock?.FontFamily?.Source}/{(textBlock is null ? "none" : Format(textBlock.FontSize))} " +
                        $"textMargin={textBlock?.Margin} textAlignment={textBlock?.HorizontalAlignment}/{textBlock?.VerticalAlignment} " +
                        $"renderTransform={Transform(transform)} layoutTransform={Transform(textBlock?.LayoutTransform)} " +
                        $"buttonTemplate={button.Template?.GetType().Name ?? "null"} " +
                        $"presenterTemplate={presenter?.ContentTemplate?.GetType().Name ?? "null"}");
                }));
            }

            // NAV_ARROW_DIAGNOSTICS_VISIBILITY_INVARIANT: A persistent floating search bar
            // may load before debug logging is enabled. Each later visible transition must
            // capture its laid-out buttons without changing their presentation or behavior.
            button.Loaded += (_, _) => RecordAfterLayout();
            button.IsVisibleChanged += (_, args) =>
            {
                if (args.NewValue is true) RecordAfterLayout();
            };
        }

        private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
        {
            if (root is null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) return match;
                var nested = FindDescendant<T>(child);
                if (nested is not null) return nested;
            }
            return null;
        }
    }
}
