// SEMANTIC ANCHOR: Ribbon-Test overflow is deterministic and single-row. Preserve existing buttons,
// routed Click handlers and group ordering. All captions use the fixed bottom row in MainWindow.xaml.
// Measure original widths once, use actual host width for the right-to-left overflow, and never
// change element parents or mutate layout during LayoutUpdated. Overflow icons mirror source glyphs;
// never maintain separate icon mappings or move icon visuals between WPF parents.
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Documents;

namespace PewDF
{
    public partial class MainWindow
    {
        private sealed class PreviewCommand
        {
            public Button Button { get; init; } = null!;
            public double Width { get; init; }
            public string Label { get; init; } = string.Empty;
            public string Id { get; init; } = string.Empty;
        }

        private readonly List<List<PreviewCommand>> _previewCommands = new();
        private readonly List<Button> _previewOverflow = new();
        // SEMANTIC ANCHOR: HOME_RIBBON_CUSTOMIZATION_SSOT
        // Stable command IDs are original group/index pairs. User layout stores only these IDs;
        // original WPF buttons remain owners of styles, tooltips and Click handlers.
        // Manual overflow and responsive overflow are distinct; neither removes a command.
        private readonly HashSet<string> _previewPinnedOverflow = new(StringComparer.Ordinal);
        private readonly Dictionary<string, EditTool> _previewToolMap = new(StringComparer.Ordinal);
        private readonly Dictionary<Button, PreviewCommand> _previewEntryByButton = new();
        // SEMANTIC ANCHOR: HOME_RIBBON_DEBUG_EVENT_INVARIANT
        // Trace identity, membership, visibility and transition; never user documents.
        // Diagnostic snapshots are read-only, and logging never decides layout or drag success.
        private void TracePreview(string stage, string detail = "")
        {
            if (!Services.DebugLogging.Enabled) return;
            string groups = string.Join(" | ", _previewCommands.Select((g, index) =>
                $"{PreviewGroupNames[index]}=[{string.Join(",", g.Select(c =>
                    c.Id + ":" + c.Label + ":" + c.Button.Visibility +
                    (_previewPinnedOverflow.Contains(c.Id) ? ":PIN" : "")))}]"));
            var ids = _previewCommands.SelectMany(g => g).Select(c => c.Id).ToArray();
            var duplicates = ids.GroupBy(id => id).Where(g => g.Count() != 1)
                .Select(g => g.Key).ToArray();
            var missing = _previewEntryByButton.Values.Select(c => c.Id).Except(ids).ToArray();
            Services.DebugLogging.Write("ribbon-debug.log",
                $"{stage} | {detail} | customize={_previewCustomizeMode} | width={RibbonPreviewHost.ActualWidth:F1} | overflow=[{string.Join(",", _previewOverflow.Select(b => _previewEntryByButton.TryGetValue(b, out var c) ? c.Id : "?"))}] | duplicates=[{string.Join(",", duplicates)}] | missing=[{string.Join(",", missing)}] | {groups}");
        }

        private bool _previewCustomizeMode;
        private Point _previewDragOrigin;
        private object? _previewDragCandidate;
        private string? _previewLastDragSlot;
        // SEMANTIC ANCHOR: HOME_DRAG_FEEDBACK_INVARIANT
        // Feedback is temporary visual state only. No command list/parent changes until Drop.
        private Popup? _previewDragGhost;
        private PreviewInsertionAdorner? _previewInsertionAdorner;
        private AdornerLayer? _previewInsertionLayer;
        private StackPanel? _previewInsertionPanel;

        private sealed class PreviewInsertionAdorner : Adorner
        {
            internal double X { get; set; }
            public PreviewInsertionAdorner(UIElement adorned) : base(adorned) => IsHitTestVisible = false;
            protected override void OnRender(DrawingContext dc)
            {
                var pen = new Pen(Brushes.DodgerBlue, 3);
                dc.DrawLine(pen, new Point(X, 2), new Point(X, Math.Max(12, AdornedElement.RenderSize.Height - 2)));
            }
        }

        private void ClearPreviewDragFeedback()
        {
            if (_previewInsertionAdorner != null && _previewInsertionLayer != null)
                _previewInsertionLayer.Remove(_previewInsertionAdorner);
            _previewInsertionLayer = null;
            _previewInsertionAdorner = null;
            _previewInsertionPanel = null;
            _previewLastDragSlot = null;
            if (_previewDragGhost != null)
            {
                _previewDragGhost.IsOpen = false;
                _previewDragGhost = null;
            }
        }

        private void ShowPreviewInsertion(StackPanel panel, int index)
        {
            if (!ReferenceEquals(_previewInsertionPanel, panel))
            {
                if (_previewInsertionAdorner != null && _previewInsertionLayer != null)
                    _previewInsertionLayer.Remove(_previewInsertionAdorner);
                _previewInsertionPanel = panel;
                _previewInsertionLayer = AdornerLayer.GetAdornerLayer(panel);
                _previewInsertionAdorner = _previewInsertionLayer == null ? null : new PreviewInsertionAdorner(panel);
                if (_previewInsertionAdorner != null) _previewInsertionLayer!.Add(_previewInsertionAdorner);
            }
            if (_previewInsertionAdorner is null) return;
            var visible = _previewCommands[Array.IndexOf(RibbonPreviewPanels, panel)]
                .Where(c => c.Button.Visibility == Visibility.Visible).ToList();
            var target = _previewCommands[Array.IndexOf(RibbonPreviewPanels, panel)];
            var next = target.Skip(index).FirstOrDefault(c => c.Button.Visibility == Visibility.Visible);
            var x = next is null ? visible.LastOrDefault()?.Button.TranslatePoint(
                new Point(visible.Last().Button.ActualWidth, 0), panel).X ?? 3
                : next.Button.TranslatePoint(new Point(0, 0), panel).X;
            _previewInsertionAdorner.X = Math.Clamp(x, 2, Math.Max(2, panel.ActualWidth - 2));
            _previewInsertionAdorner.InvalidateVisual();
        }
        private static string PreviewLayoutFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PewDF", "home-ribbon-layout.json");
        private static readonly string[] PreviewGroupNames = { "File", "Edit", "Draw", "Pages", "Document", "Forms" };
        private sealed class SavedPreviewLayout
        {
            public int Version { get; set; } = 1;
            public List<List<string>> Groups { get; set; } = new();
            public List<string> Overflow { get; set; } = new();
        }

        private bool _previewReflowQueued;
        private bool _previewInitialized;
        private double[] _previewGroupOverheads = Array.Empty<double>();
        private double[] _previewDividerWidths = Array.Empty<double>();

        private StackPanel[] RibbonPreviewPanels => new[]
        {
            RibbonPreviewFile, RibbonPreviewEdit, RibbonPreviewDraw,
            RibbonPreviewPages, RibbonPreviewDocument, RibbonPreviewForms
        };

        private void RibbonPreview_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_previewInitialized)
            {
                var panels = RibbonPreviewPanels;
                foreach (var panel in panels)
                {
                    var items = new List<PreviewCommand>();
                    foreach (var button in panel.Children.OfType<Button>())
                    {
                        button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        var label = (button.Content as Panel)?.Children.OfType<TextBlock>().LastOrDefault()?.Text
                                    ?? button.ToolTip?.ToString() ?? "Werkzeug";
                        var id = $"{_previewCommands.Count}:{items.Count}";
                        var entry = new PreviewCommand { Button = button, Width = button.DesiredSize.Width, Label = label, Id = id };
                        items.Add(entry);
                        _previewEntryByButton[button] = entry;
                    }
                    _previewCommands.Add(items);
                }
                // Preserve the existing Grid/caption margins and the separators between groups.
                var grids = panels.Select(p => (Grid)p.Parent).ToArray();
                _previewGroupOverheads = grids.Select(g => g.Margin.Left + g.Margin.Right).ToArray();
                _previewDividerWidths = grids.Select(g =>
                {
                    var border = (g.Parent as Panel)?.Children.Cast<UIElement>()
                        .SkipWhile(e => !ReferenceEquals(e, g)).Skip(1).OfType<Border>().FirstOrDefault();
                    return border == null ? 0d : border.Width + border.Margin.Left + border.Margin.Right;
                }).ToArray();
                foreach (var panel in panels)
                {
                    // SEMANTIC ANCHOR: HOME_EMPTY_GROUP_DROP_TARGET_INVARIANT
                    // An empty group still has a hit-testable Grid drop zone while customizing.
                    // Restore its normal dimensions/visibility outside customization mode.
                    var host = (Grid)panel.Parent;
                    host.AllowDrop = true;
                    host.DragOver += PreviewPanel_DragOver;
                    host.Drop += PreviewPanel_Drop;
                    panel.AllowDrop = true;
                    panel.DragOver += PreviewPanel_DragOver;
                    panel.Drop += PreviewPanel_Drop;
                }
                foreach (var item in _previewCommands.SelectMany(g => g))
                {
                    item.Button.PreviewMouseRightButtonUp += PreviewButton_RightClick;
                    item.Button.PreviewMouseLeftButtonDown += PreviewButton_MouseDown;
                    item.Button.PreviewMouseMove += PreviewButton_MouseMove;
                }
                InitializePreviewCustomization();
                _previewInitialized = true;
            }
            UpdateRibbonPreviewSelection();
            QueuePreviewReflow();
        }

        // SEMANTIC ANCHOR: The preview mirrors _currentTool; it does not own tool state.
        // Button tags are only presentation flags, including when commands move into overflow.
        private void UpdateRibbonPreviewSelection()
        {
            if (!_previewInitialized) return;
            foreach (var group in _previewCommands)
                foreach (var entry in group)
                    entry.Button.Tag = _previewToolMap.TryGetValue(entry.Id, out var tool)
                        && _currentTool == tool ? "selected" : null;
            foreach (var menuItem in RibbonPreviewOverflowMenu.Items.OfType<MenuItem>())
            {
                if (menuItem.Tag is Button source)
                {
                    if (Equals(source.Tag, "selected"))
                        menuItem.SetResourceReference(Control.BackgroundProperty, "SelectionBg");
                    else menuItem.ClearValue(Control.BackgroundProperty);
                }
            }
        }

        // SEMANTIC ANCHOR: HOME_VIEWPORT_DIAGNOSTICS_OBSERVABILITY_INVARIANT
        // Diagnostics observe transitions only; they must never mutate layout, input handling,
        // command membership or persisted settings. Button IDs remain the sole identity (SSOT).
        // Log viewport measurements and each effective clipping-state change to distinguish
        // layout feedback, scroll motion, and a real command membership transition.
        private readonly Dictionary<string, string> _previewViewportLastStates = new(StringComparer.Ordinal);
        private long _previewViewportDiagnosticSequence;

        private void TraceViewportTransition(string stage, string id, string before, string after,
            double left, double right, double viewportWidth, double offset, bool clipping)
        {
            if (!Services.DebugLogging.Enabled) return;
            Services.DebugLogging.Write("ribbon-debug.log",
                $"VIEWPORT_{stage} | seq={++_previewViewportDiagnosticSequence}; id={id}; " +
                $"before={before}; after={after}; left={left:F2}; right={right:F2}; " +
                $"viewportWidth={viewportWidth:F2}; offset={offset:F2}; " +
                $"clipEnabled={clipping}; customize={_previewCustomizeMode}; " +
                $"hostWidth={RibbonPreviewHost.ActualWidth:F2}; timeUtc={DateTime.UtcNow:O}");
        }

        // SEMANTIC ANCHOR: HOME_CUSTOMIZE_VIEWPORT_COMMAND_BOUNDARY_INVARIANT
        // The ScrollViewer alone clips the visible viewport while keeping all group commands
        // fully rendered and hit-testable within their actual bounds. Never toggle opacity,
        // hit testing, membership or persisted layout based on transient viewport measurements.
        // Scrolling provides access to offscreen commands; responsive overflow remains separate.
        private void RibbonPreviewCustomizeViewport_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (!_previewInitialized) return;
            if (Services.DebugLogging.Enabled && (e.HorizontalChange != 0 || e.ViewportWidthChange != 0 || e.ExtentWidthChange != 0))
                Services.DebugLogging.Write("ribbon-debug.log",
                    $"VIEWPORT_SCROLL_EVENT | offset={e.HorizontalOffset:F2}; delta={e.HorizontalChange:F2}; " +
                    $"viewport={e.ViewportWidth:F2}; viewportDelta={e.ViewportWidthChange:F2}; " +
                    $"extent={e.ExtentWidth:F2}; extentDelta={e.ExtentWidthChange:F2}; customize={_previewCustomizeMode}");
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdatePreviewViewportCommandClipping));
        }

        private void UpdatePreviewViewportCommandClipping()
        {
            if (!_previewInitialized) return;
            var viewport = RibbonPreviewCustomizeViewport;
            // Viewport-space coordinates, unlike stored group indices, are allowed to change
            // with scrolling and resizing. Never use this visibility feedback as layout state.
            double leftEdge = 0;
            double rightEdge = viewport.ViewportWidth;
            bool clip = _previewCustomizeMode && rightEdge > 0;
            foreach (var command in _previewCommands.SelectMany(group => group))
            {
                var button = command.Button;
                bool eligible = clip && button.Visibility == Visibility.Visible && button.IsLoaded;
                double left = double.NaN;
                double right = double.NaN;
                bool partiallyClipped = false;
                if (eligible)
                {
                    Point bounds = button.TranslatePoint(new Point(0, 0), viewport);
                    left = bounds.X;
                    right = bounds.X + button.ActualWidth;
                    partiallyClipped = (left < leftEdge - 0.5 && right > leftEdge + 0.5)
                        || (left < rightEdge - 0.5 && right > rightEdge + 0.5);
                }
                string nextState = !eligible ? "inactive" : partiallyClipped ? "clipped" : "fully-visible";
                if (Services.DebugLogging.Enabled &&
                    (!_previewViewportLastStates.TryGetValue(command.Id, out var previousState)
                     || previousState != nextState))
                    TraceViewportTransition("STATE_CHANGE", command.Id, previousState ?? "unobserved",
                        nextState, left, right, rightEdge, viewport.HorizontalOffset, clip);
                _previewViewportLastStates[command.Id] = nextState;

                // Read-only diagnostics: viewport measurements must never change UI state.
                // The ScrollViewer is the SSOT for viewport clipping and hit testing.
            }
        }

        private void RibbonPreview_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) >= 1)
                QueuePreviewReflow();
        }

        private void QueuePreviewReflow()
        {
            if (_previewReflowQueued || !_previewInitialized) return;
            _previewReflowQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _previewReflowQueued = false;
                ReflowRibbonPreview();
            }));
        }

        private void ReflowRibbonPreview()
        {
            if (!RibbonPreviewHost.IsLoaded || !_previewInitialized) return;
            TracePreview("REFLOW_BEGIN");
            var available = RibbonPreviewHost.ActualWidth - RibbonPreviewGroups.Margin.Left
                          - RibbonPreviewGroups.Margin.Right - 6;
            if (available <= 0) return;

            // SEMANTIC ANCHOR: HOME_OVERFLOW_COMMAND_SSOT
            // Manually pinned commands stay in overflow at every width. Responsive hiding
            // is computed afresh at each reflow, from right to left, without persisting it.
            var visibleCounts = _previewCommands.Select(g => g.Count(c => !_previewPinnedOverflow.Contains(c.Id))).ToArray();
            double UsedWidth()
            {
                double width = 0;
                for (int i = 0; i < visibleCounts.Length; i++)
                {
                    if (visibleCounts[i] == 0)
                    {
                        // Editing must not reserve 72px for an empty group and unexpectedly
                        // evict previously visible commands merely on entering Customize.
                        // The drop target remains rendered; responsive overflow is logged.
                        if (_previewCustomizeMode) width += 24 + _previewGroupOverheads[i] + _previewDividerWidths[i];
                        continue;
                    }
                    width += _previewGroupOverheads[i] + _previewDividerWidths[i];
                    width += _previewCommands[i].Where(c => !_previewPinnedOverflow.Contains(c.Id))
                        .Take(visibleCounts[i]).Sum(c => c.Width);
                }
                return width;
            }
            // Document Info always keeps the overflow button present.
            double limit = available - RibbonPreviewOverflowButton.Width
                          - RibbonPreviewOverflowButton.Margin.Left - RibbonPreviewOverflowButton.Margin.Right;
            // SEMANTIC ANCHOR: HOME_CUSTOMIZATION_VISIBILITY_INVARIANT
            // Editing never performs responsive eviction: a committed command stays visible
            // in its target group. The viewport scrolls instead of silently hiding controls.
            // Normal mode retains its original right-to-left responsive overflow behavior.
            RibbonPreviewCustomizeViewport.HorizontalScrollBarVisibility = _previewCustomizeMode
                ? ScrollBarVisibility.Visible : ScrollBarVisibility.Disabled;
            if (!_previewCustomizeMode)
            {
                for (int group = visibleCounts.Length - 1; group >= 0 && UsedWidth() > limit;)
                {
                    if (visibleCounts[group] > 0) visibleCounts[group]--;
                    else group--;
                }
            }

            // Keep the Save split-button's main action and arrow visible together,
            // including when responsive overflow is triggered in a customized group.
            for (int group = 0; group < _previewCommands.Count; group++)
            {
                var availableCommands = _previewCommands[group]
                    .Where(c => !_previewPinnedOverflow.Contains(c.Id)).ToList();
                int saveIndex = availableCommands.FindIndex(c => c.Id == "0:1");
                int arrowIndex = availableCommands.FindIndex(c => c.Id == "0:2");
                if (saveIndex >= 0 && arrowIndex == saveIndex + 1 && visibleCounts[group] == arrowIndex)
                    visibleCounts[group]--;
            }

            _previewOverflow.Clear();
            var panels = RibbonPreviewPanels;
            for (int i = 0; i < panels.Length; i++)
            {
                var visibleIndex = 0;
                foreach (var entry in _previewCommands[i])
                {
                    bool pinned = _previewPinnedOverflow.Contains(entry.Id);
                    bool shown = !pinned && visibleIndex++ < visibleCounts[i];
                    entry.Button.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
                    if (!shown) _previewOverflow.Add(entry.Button);
                }
                var grid = (Grid)panels[i].Parent;
                // SEMANTIC ANCHOR: HOME_EMPTY_GROUP_DROP_TARGET_INVARIANT
                // Empty editing groups are explicit drop targets, never commands or persisted
                // layout changes. Scrolling avoids displacing visible commands out of view.
                // Do not add a placeholder command: the group's identity stays in its grid.
                grid.MinWidth = _previewCustomizeMode && visibleCounts[i] == 0 ? 24 : 0;
                grid.Background = _previewCustomizeMode && visibleCounts[i] == 0
                    ? Brushes.Transparent : null;
                grid.Visibility = visibleCounts[i] > 0 || _previewCustomizeMode
                    ? Visibility.Visible : Visibility.Collapsed;
                // Each group is followed by its own divider in the same StackPanel.
                if (grid.Parent is Panel parent)
                {
                    var index = parent.Children.IndexOf(grid);
                    if (index + 1 < parent.Children.Count && parent.Children[index + 1] is Border divider)
                        divider.Visibility = visibleCounts[i] > 0 || _previewCustomizeMode
                            ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            // SEMANTIC ANCHOR: DOCUMENT_INFO_OVERFLOW_ALWAYS. The Startseite overflow always
            // exposes document information, independently of available width. The Datei
            // ribbon command remains the primary visible entry; no duplicate command row.
            RibbonPreviewOverflowButton.Visibility = Visibility.Visible;
            RibbonPreviewOverflowMenu.Items.Clear();
            foreach (var item in _previewCommands.SelectMany(group => group).Where(x => _previewOverflow.Contains(x.Button)))
            {
                var source = item.Button;
                // SEMANTIC ANCHOR: Never add spacing on MenuItem.Icon: WPF has a fixed icon column
                // and a margin there clips the icon. Put the gap on the text header only.
                var header = new TextBlock { Text = (source.Content as Panel)?.Children.OfType<TextBlock>().LastOrDefault()?.Text ?? item.Label, Margin = new Thickness(12, 0, 0, 0) };
                var menuItem = new MenuItem { Header = header, IsEnabled = source.IsEnabled, Tag = source };
                if (Equals(source.Tag, "selected"))
                    menuItem.SetResourceReference(Control.BackgroundProperty, "SelectionBg");
                // Use the exact same glyph as the ribbon button; create a separate visual
                // because a WPF element cannot belong to two visual parents.
                if (source.Content is Panel content && content.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock glyph)
                {
                    var icon = new TextBlock
                    {
                        Text = glyph.Text,
                        FontFamily = glyph.FontFamily,
                        FontSize = 16,
                        FontStyle = glyph.FontStyle,
                        FontWeight = glyph.FontWeight,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    icon.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                    menuItem.Icon = icon;
                }
                menuItem.Click += (_, _) =>
                {
                    if (!_previewCustomizeMode && source.IsEnabled)
                        source.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, source));
                };
                if (_previewCustomizeMode && item.Id != "0:2")
                {
                    menuItem.PreviewMouseLeftButtonDown += (sender, e) =>
                    {
                        _previewDragCandidate = item;
                        _previewDragOrigin = e.GetPosition(menuItem);
                    };
                    // The context menu is a separate WPF Popup visual tree: use its
                    // own pointer coordinates and drag source, not the ribbon host.
                    menuItem.PreviewMouseMove += (sender, e) => BeginPreviewDrag(e, item, menuItem);
                    var moveMenu = new ContextMenu();
                    for (int target = 0; target < PreviewGroupNames.Length; target++)
                    {
                        int to = target;
                        var move = new MenuItem { Header = "Move to " + PreviewGroupNames[to] };
                        move.Click += (_, _) => MovePreviewCommand(item, to);
                        moveMenu.Items.Add(move);
                    }
                    menuItem.ContextMenu = moveMenu;
                }
                RibbonPreviewOverflowMenu.Items.Add(menuItem);
            }
            var documentInfoItem = new MenuItem
            {
                Header = new TextBlock { Text = Loc("Str_Ribbon_Dokumenteninfo"), Margin = new Thickness(12, 0, 0, 0) },
                Icon = new TextBlock { Text = "", FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 16 },
                IsEnabled = _doc is not null
            };
            ((TextBlock)documentInfoItem.Icon).SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            documentInfoItem.Click += DocInfo_Click;
            RibbonPreviewOverflowMenu.Items.Add(documentInfoItem);
            RibbonPreviewOverflowMenu.Items.Add(new Separator());
            var customizeItem = new MenuItem
            {
                Header = _previewCustomizeMode ? "Done customizing" : "Customize ribbon…"
            };
            customizeItem.Click += (_, _) =>
            {
                TracePreview("CUSTOMIZE_BEFORE");
                _previewCustomizeMode = !_previewCustomizeMode;
                // SEMANTIC ANCHOR: HOME_MODE_TRANSITION_INVARIANT
                // Switching modes changes presentation only, never command ownership,
                // manual overflow pins, or saved layout. Restore the initial scroll
                // position when returning to the responsive normal-mode viewport.
                if (!_previewCustomizeMode)
                    RibbonPreviewCustomizeViewport.ScrollToHorizontalOffset(0);
                TracePreview("CUSTOMIZE_AFTER");
                QueuePreviewReflow();
            };
            RibbonPreviewOverflowMenu.Items.Add(customizeItem);
            TracePreview("REFLOW_END", $"visibleCounts={string.Join(",", visibleCounts)}; available={available:F1}; limit={limit:F1}");
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdatePreviewViewportCommandClipping));
            if (_previewCustomizeMode)
            {
                var resetItem = new MenuItem { Header = "Restore default ribbon" };
                resetItem.Click += (_, _) => ResetPreviewLayout();
                RibbonPreviewOverflowMenu.Items.Add(resetItem);
            }
        }

        private void RibbonPreviewOverflow_Click(object sender, RoutedEventArgs e)
        {
            foreach (var menuItem in RibbonPreviewOverflowMenu.Items.OfType<MenuItem>())
            {
                if (menuItem.Tag is Button source)
                    menuItem.IsEnabled = source.IsEnabled;
            }
            RibbonPreviewOverflowMenu.PlacementTarget = RibbonPreviewOverflowButton;
            RibbonPreviewOverflowMenu.IsOpen = true;
        }

        // SEMANTIC ANCHOR: HOME_RIBBON_LAYOUT_MIGRATION_INVARIANT
        // Unknown/stale IDs are ignored; new commands retain their default group/order.
        // Invalid user data never blocks startup. Reset deletes overrides, not commands.
        private void InitializePreviewCustomization()
        {
            var mapping = new (string id, EditTool tool)[]
            {
                ("1:0", EditTool.Select), ("1:1", EditTool.Text),
                ("1:2", EditTool.Highlight), ("1:3", EditTool.Image),
                ("1:4", EditTool.Signature), ("2:0", EditTool.Line),
                ("2:1", EditTool.Shape), ("2:2", EditTool.Draw),
                ("2:3", EditTool.Measure), ("3:2", EditTool.Crop),
                ("4:4", EditTool.Rotate), ("5:0", EditTool.FormField)
            };
            foreach (var (id, tool) in mapping) _previewToolMap[id] = tool;
            ApplyDefaultPreviewOverflow();
            try
            {
                if (!File.Exists(PreviewLayoutFile)) return;
                var stored = JsonSerializer.Deserialize<SavedPreviewLayout>(File.ReadAllText(PreviewLayoutFile));
                if (stored?.Version != 1 || stored.Groups.Count != _previewCommands.Count) return;
                var all = _previewCommands.SelectMany(x => x).ToDictionary(x => x.Id);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int group = 0; group < stored.Groups.Count; group++)
                {
                    var reordered = new List<PreviewCommand>();
                    foreach (var id in stored.Groups[group])
                        if (all.TryGetValue(id, out var entry) && seen.Add(id)) reordered.Add(entry);
                    _previewCommands[group].Clear();
                    _previewCommands[group].AddRange(reordered);
                }
                // Unrecognized or newly added commands revert to their original group.
                foreach (var (id, entry) in all)
                {
                    if (seen.Add(id) && int.TryParse(id.Split(':')[0], out var originalGroup)
                        && originalGroup >= 0 && originalGroup < _previewCommands.Count)
                        _previewCommands[originalGroup].Add(entry);
                }
                // Repair split Save/Save As layout if it came from an older/invalid profile.
                var save = all["0:1"];
                var arrow = all["0:2"];
                foreach (var group in _previewCommands) group.Remove(arrow);
                var saveGroup = _previewCommands.First(g => g.Contains(save));
                saveGroup.Insert(saveGroup.IndexOf(save) + 1, arrow);
                _previewPinnedOverflow.Clear();
                foreach (var id in stored.Overflow)
                    if (all.ContainsKey(id)) _previewPinnedOverflow.Add(id);
                if (_previewPinnedOverflow.Contains("0:1") || _previewPinnedOverflow.Contains("0:2"))
                {
                    _previewPinnedOverflow.Add("0:1");
                    _previewPinnedOverflow.Add("0:2");
                }
                SyncPreviewPanelOrder();
                TracePreview("LAYOUT_LOADED", PreviewLayoutFile);
            }
            catch (Exception ex) { TracePreview("LAYOUT_LOAD_FAILED", ex.ToString()); ApplyDefaultPreviewOverflow(); }
        }

        private void ApplyDefaultPreviewOverflow()
        {
            // SEMANTIC ANCHOR: HOME_OVERFLOW_DEFAULT_RESPONSIVE_INVARIANT
            // Default commands are never pinned merely because they initially need
            // overflow space. Only explicit user customization may persist pins;
            // ReflowRibbonPreview is the SSOT for width-dependent overflow.
            // Saved layout pins remain authoritative and are not cleared on load.
            _previewPinnedOverflow.Clear();
        }

        private void SyncPreviewPanelOrder()
        {
            var panels = RibbonPreviewPanels;
            for (int i = 0; i < panels.Length; i++)
            {
                var panel = panels[i];
                foreach (var entry in _previewCommands[i])
                {
                    if (entry.Button.Parent is Panel oldParent) oldParent.Children.Remove(entry.Button);
                    panel.Children.Add(entry.Button);
                }
            }
            // SEMANTIC ANCHOR: RIBBON_COMPOSITE_COMMAND_ORDER_INVARIANT
            // The Save and Mark split controls are Grid children, not direct Buttons.
            // A restored button-only customization must not move these composite controls
            // ahead of their original neighbors: Open -> Save and Select -> Text -> Mark.
            // Keep the original command IDs and the existing split-button behavior unchanged.
            RestoreCompositeRibbonOrder(RibbonPreviewFile, "0:0", 0);
            RestoreCompositeRibbonOrder(RibbonPreviewEdit, "1:1", 0);
        }

        private void RestoreCompositeRibbonOrder(StackPanel panel, string precedingCommandId, int compositeIndex)
        {
            var composites = panel.Children.OfType<Grid>().ToList();
            if (compositeIndex >= composites.Count) return;
            var composite = composites[compositeIndex];
            var preceding = _previewEntryByButton.Values.FirstOrDefault(c => c.Id == precedingCommandId)?.Button;
            if (preceding is null || !ReferenceEquals(preceding.Parent, panel)) return;
            panel.Children.Remove(composite);
            panel.Children.Insert(panel.Children.IndexOf(preceding) + 1, composite);
        }

        private void SavePreviewLayout()
        {
            TracePreview("LAYOUT_SAVE_BEGIN");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PreviewLayoutFile)!);
                var state = new SavedPreviewLayout
                {
                    Groups = _previewCommands.Select(g => g.Select(c => c.Id).ToList()).ToList(),
                    Overflow = _previewPinnedOverflow.OrderBy(s => s, StringComparer.Ordinal).ToList()
                };
                File.WriteAllText(PreviewLayoutFile, JsonSerializer.Serialize(state));
                TracePreview("LAYOUT_SAVE_SUCCESS", PreviewLayoutFile);
            }
            catch (IOException) { /* In-memory layout remains usable if the user profile is read-only. */ }
            catch (UnauthorizedAccessException) { /* No destructive fallback. */ }
        }

        private void MovePreviewCommand(PreviewCommand item, int targetGroup, int? targetIndex = null)
        {
            TracePreview("MOVE_REQUEST", $"id={item.Id}; targetGroup={targetGroup}; targetIndex={targetIndex}");
            if (!_previewCustomizeMode || targetGroup < -1 || targetGroup >= _previewCommands.Count)
            {
                TracePreview("MOVE_REJECTED", $"id={item.Id}; targetGroup={targetGroup}");
                return;
            }
            // Save and its adjacent Save As arrow stay in one original group and in their
            // original relative order; do not separate this split control.
            // SEMANTIC ANCHOR: HOME_SAVE_SPLIT_CONTROL_INVARIANT
            // Save and Save As are one visual action: never split the arrow from Save.
            if (item.Id == "0:2") return;
            PreviewCommand? companion = item.Id == "0:1"
                ? _previewCommands.SelectMany(g => g).FirstOrDefault(c => c.Id == "0:2") : null;
            // SEMANTIC ANCHOR: HOME_COMMAND_MEMBERSHIP_TRANSACTION_INVARIANT
            // A user edit is atomic: every registered command ID must remain owned by
            // exactly one group. Reject invalid sources; roll back an invalid candidate
            // before changing WPF parents or persisting user configuration.
            var originalGroups = _previewCommands.Select(g => g.ToList()).ToArray();
            var originalPins = new HashSet<string>(_previewPinnedOverflow, StringComparer.Ordinal);
            var expectedIds = _previewEntryByButton.Values.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            if (!_previewEntryByButton.TryGetValue(item.Button, out var registered) ||
                !ReferenceEquals(registered, item) ||
                _previewCommands.Sum(g => g.Count(c => ReferenceEquals(c, item))) != 1 ||
                (companion != null && _previewCommands.Sum(g => g.Count(c => ReferenceEquals(c, companion))) != 1))
            {
                TracePreview("MOVE_REJECTED_INVALID_SOURCE", $"id={item.Id}");
                return;
            }
            // SEMANTIC ANCHOR: HOME_DROP_INDEX_INVARIANT
            // Drop index is computed before removal. Adjust when moving forwards within
            // the same group; a canceled/invalid drag must never mutate command ownership.
            var oldGroup = _previewCommands.FindIndex(g => g.Contains(item));
            var oldIndex = oldGroup >= 0 ? _previewCommands[oldGroup].IndexOf(item) : -1;
            if (targetGroup == oldGroup && targetIndex.HasValue && oldIndex >= 0 && oldIndex < targetIndex.Value)
                targetIndex--;
            foreach (var group in _previewCommands)
            {
                group.Remove(item);
                if (companion != null) group.Remove(companion);
            }
            if (targetGroup < 0) _previewPinnedOverflow.Add(item.Id);
            else
            {
                _previewPinnedOverflow.Remove(item.Id);
                var dest = _previewCommands[targetGroup];
                int insertion = Math.Clamp(targetIndex ?? dest.Count, 0, dest.Count);
                dest.Insert(insertion, item);
                if (companion != null) dest.Insert(insertion + 1, companion);
            }
            if (companion != null)
            {
                if (targetGroup < 0) _previewPinnedOverflow.Add(companion.Id);
                else _previewPinnedOverflow.Remove(companion.Id);
            }
            // A manually overflowed command still requires ownership by a hidden host.
            if (targetGroup < 0)
            {
                int original = int.Parse(item.Id.Split(':')[0]);
                _previewCommands[original].Add(item);
                if (companion != null) _previewCommands[original].Add(companion);
            }
            var candidateIds = _previewCommands.SelectMany(g => g).Select(c => c.Id).ToArray();
            var saveOwner = _previewCommands.FindIndex(g => g.Any(c => c.Id == "0:1"));
            var splitPairValid = saveOwner >= 0 &&
                _previewCommands[saveOwner].FindIndex(c => c.Id == "0:2") ==
                _previewCommands[saveOwner].FindIndex(c => c.Id == "0:1") + 1 &&
                _previewPinnedOverflow.Contains("0:1") == _previewPinnedOverflow.Contains("0:2");
            if (candidateIds.Length != expectedIds.Count ||
                candidateIds.Distinct(StringComparer.Ordinal).Count() != candidateIds.Length ||
                !expectedIds.SetEquals(candidateIds) || !splitPairValid)
            {
                for (int i = 0; i < _previewCommands.Count; i++)
                {
                    _previewCommands[i].Clear();
                    _previewCommands[i].AddRange(originalGroups[i]);
                }
                _previewPinnedOverflow.Clear();
                _previewPinnedOverflow.UnionWith(originalPins);
                TracePreview("MOVE_ROLLBACK_INVARIANT", $"id={item.Id}; targetGroup={targetGroup}; targetIndex={targetIndex}");
                return;
            }
            SyncPreviewPanelOrder();
            TracePreview("MOVE_APPLIED", $"id={item.Id}; from={oldGroup}:{oldIndex}; to={targetGroup}:{targetIndex}");
            SavePreviewLayout();
            QueuePreviewReflow();
        }

        private void ResetPreviewLayout()
        {
            TracePreview("RESET_REQUEST");
            if (!_previewCustomizeMode) return;
            var all = _previewCommands.SelectMany(g => g).ToArray();
            foreach (var group in _previewCommands) group.Clear();
            foreach (var entry in all)
            {
                int index = int.Parse(entry.Id.Split(':')[0]);
                _previewCommands[index].Add(entry);
            }
            foreach (var group in _previewCommands)
                group.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            ApplyDefaultPreviewOverflow();
            SyncPreviewPanelOrder();
            try { File.Delete(PreviewLayoutFile); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            QueuePreviewReflow();
        }

        // SEMANTIC ANCHOR: HOME_DRAG_SOURCE_COORDINATE_INVARIANT
        // Pointer origin, movement and DoDragDrop source must use the same visual,
        // including menu items in the Overflow ContextMenu's separate Popup.
        private void BeginPreviewDrag(MouseEventArgs e, PreviewCommand item, UIElement source)
        {
            if (!_previewCustomizeMode || e.LeftButton != MouseButtonState.Pressed) return;
            var position = e.GetPosition(source);
            if (!ReferenceEquals(_previewDragCandidate, item) ||
                (Math.Abs(position.X - _previewDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
                 Math.Abs(position.Y - _previewDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance))
                return;
            _previewDragCandidate = null;
            TracePreview("DRAG_START", $"id={item.Id}; source={source.GetType().Name}; origin={_previewDragOrigin}; current={position}");
            // The visual follows WPF's mouse placement without modifying the source Button.
            _previewDragGhost = new Popup
            {
                Placement = PlacementMode.MousePoint,
                AllowsTransparency = true,
                IsHitTestVisible = false,
                StaysOpen = true,
                HorizontalOffset = 12,
                VerticalOffset = 12,
                Child = new Border
                {
                    Opacity = 0.68,
                    Background = Brushes.DimGray,
                    BorderBrush = Brushes.DodgerBlue,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12, 7, 12, 7),
                    Child = new TextBlock { Text = item.Label, Foreground = Brushes.White }
                }
            };
            try
            {
                _previewDragGhost.IsOpen = true;
                var effect = DragDrop.DoDragDrop(source, new DataObject(typeof(PreviewCommand), item), DragDropEffects.Move);
                TracePreview("DRAG_FINISH", $"id={item.Id}; effect={effect}");
            }
            finally { ClearPreviewDragFeedback(); }
        }

        private void ClearPreviewInsertionOnly()
        {
            if (_previewInsertionAdorner != null && _previewInsertionLayer != null)
                _previewInsertionLayer.Remove(_previewInsertionAdorner);
            _previewInsertionAdorner = null;
            _previewInsertionLayer = null;
            _previewInsertionPanel = null;
        }

        private StackPanel? ResolvePreviewDropPanel(object sender) => sender switch
        {
            StackPanel p => p,
            Grid g => RibbonPreviewPanels.FirstOrDefault(p => ReferenceEquals(p.Parent, g)),
            _ => null
        };

        private int PreviewDropIndex(StackPanel panel, double x)
        {
            var groupIndex = Array.IndexOf(RibbonPreviewPanels, panel);
            if (groupIndex < 0) return 0;
            var commands = _previewCommands[groupIndex];
            for (int i = 0; i < commands.Count; i++)
            {
                var button = commands[i].Button;
                if (button.Visibility == Visibility.Visible && x <
                    button.TranslatePoint(new Point(button.ActualWidth / 2, 0), panel).X)
                    return i;
            }
            return commands.Count;
        }

        private void PreviewPanel_DragOver(object sender, DragEventArgs e)
        {
            bool valid = _previewCustomizeMode && e.Data.GetDataPresent(typeof(PreviewCommand));
            e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;
            if (valid && ResolvePreviewDropPanel(sender) is StackPanel panel)
            {
                int index = PreviewDropIndex(panel, e.GetPosition(panel).X);
                // Keep hover logs concise: one record for each changed target slot.
                string slot = $"{Array.IndexOf(RibbonPreviewPanels, panel)}:{index}";
                if (_previewLastDragSlot != slot)
                {
                    _previewLastDragSlot = slot;
                    TracePreview("DRAG_HOVER", $"slot={slot}; x={e.GetPosition(panel).X:F1}");
                }
                ShowPreviewInsertion(panel, index);
            }
            else ClearPreviewInsertionOnly();
            e.Handled = true;
        }

        private void PreviewPanel_Drop(object sender, DragEventArgs e)
        {
            if (!_previewCustomizeMode ||
                e.Data.GetData(typeof(PreviewCommand)) is not PreviewCommand item) return;
            StackPanel? panel = ResolvePreviewDropPanel(sender);
            if (panel is null) return;
            int target = Array.IndexOf(RibbonPreviewPanels, panel);
            if (target < 0) return;
            int index = PreviewDropIndex(panel, e.GetPosition(panel).X);
            TracePreview("DROP_RECEIVED", $"id={item.Id}; group={target}; index={index}; x={e.GetPosition(panel).X:F1}");
            ClearPreviewInsertionOnly();
            MovePreviewCommand(item, target, index);
            e.Handled = true;
        }

        private void PreviewButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_previewCustomizeMode || sender is not Button button ||
                !_previewEntryByButton.TryGetValue(button, out var entry)) return;
            if (entry.Id == "0:2") return;
            _previewDragCandidate = entry;
            TracePreview("DRAG_ARM", $"id={entry.Id}");
            // SEMANTIC ANCHOR: HOME_DRAG_SOURCE_COORDINATE_INVARIANT
            // Record the drag origin in the same visual coordinate space used by
            // BeginPreviewDrag; the ribbon host and each button have different origins.
            _previewDragOrigin = e.GetPosition(button);
            e.Handled = true;
        }

        private void PreviewButton_MouseMove(object sender, MouseEventArgs e)
        {
            if (sender is Button button && _previewEntryByButton.TryGetValue(button, out var entry) && entry.Id != "0:2")
                BeginPreviewDrag(e, entry, button);
        }

        private void PreviewButton_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Button button || !_previewEntryByButton.TryGetValue(button, out var entry)) return;
            if (entry.Id == "0:2") return;
            var menu = new ContextMenu();
            var toOverflow = new MenuItem { Header = "Move to overflow (…)" };
            toOverflow.Click += (_, _) => { _previewCustomizeMode = true; MovePreviewCommand(entry, -1); };
            menu.Items.Add(toOverflow);
            if (_previewCustomizeMode)
            {
                for (int group = 0; group < PreviewGroupNames.Length; group++)
                {
                    int target = group;
                    var move = new MenuItem { Header = "Move to " + PreviewGroupNames[target] };
                    move.Click += (_, _) => MovePreviewCommand(entry, target);
                    menu.Items.Add(move);
                }
            }
            button.ContextMenu = menu;
            e.Handled = true;
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }
}
