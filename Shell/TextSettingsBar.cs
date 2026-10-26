using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using PewDF.Services;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace PewDF
{
    public partial class MainWindow
    {
        // ============================================================
        // Text tool settings bar
        // ============================================================

        // Underline and/or strikethrough as one decoration collection (either, both, or none).
        private static TextDecorationCollection? BuildDecorations(bool underline, bool strike)
        {
            if (!underline && !strike) return null;
            var d = new TextDecorationCollection();
            if (underline) foreach (var x in TextDecorations.Underline) d.Add(x);
            if (strike) foreach (var x in TextDecorations.Strikethrough) d.Add(x);
            return d;
        }

        // Apply the current typeface + Bold/Italic/Underline/Strikethrough to an in-canvas edit box, so
        // editing stays WYSIWYG with the text bar. Bad font names fall back to Segoe UI rather than throwing.
        private void StyleEditBox(TextBox tb)
        {
            try { tb.FontFamily = new FontFamily(_textFontName); } catch { tb.FontFamily = UiKit.UiFont; }
            tb.FontWeight = _textBold ? FontWeights.Bold : FontWeights.Normal;
            tb.FontStyle = _textItalic ? FontStyles.Italic : FontStyles.Normal;
            tb.TextDecorations = BuildDecorations(_textUnderline, _textStrike);
            ApplyLetterSpacing(tb, tb.Text, CanvasLetterSpacing(tb.Tag is int page ? page : PageList.SelectedIndex));
            tb.TextChanged -= ActiveTextBox_TextChanged;
            tb.TextChanged += ActiveTextBox_TextChanged;
        }

        private void ActiveTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
                ApplyLetterSpacing(tb, tb.Text,
                    CanvasLetterSpacing(tb.Tag is int page ? page : PageList.SelectedIndex));
        }

        private double CanvasLetterSpacing(int pageIndex)
        {
            double spacing = _textLetterSpacing;
            if (_doc is not null && pageIndex >= 0 && _renderDims.TryGetValue(pageIndex, out var rd) && rd.h > 0)
            {
                double sy = EnsureEngineDocumentSession().Pages[pageIndex].Height / rd.h;
                if (sy > 0) spacing /= sy;
            }
            return spacing;
        }

        private static void ApplyLetterSpacing(DependencyObject control, string text, double spacing)
        {
            // SEMANTIC ANCHOR: INLINE_CARET_TEXT_EFFECTS_AB_TEST
            // Diagnostic only: exclude the live TextBox editor from TextEffects
            // while preserving the existing formatting of all other controls.
            // Restore after confirming whether initial caret visibility changes.
            if (control is TextBox)
            {
                control.ClearValue(TextBlock.TextEffectsProperty);
                return;
            }
            var effects = new TextEffectCollection();
            if (Math.Abs(spacing) >= .01)
            {
                int column = 0;
                var elements = System.Globalization.StringInfo.GetTextElementEnumerator(text);
                while (elements.MoveNext())
                {
                    string current = elements.GetTextElement();
                    int index = elements.ElementIndex;
                    if (current is "\r" or "\n") { column = 0; continue; }
                    effects.Add(new TextEffect
                    {
                        PositionStart = index,
                        PositionCount = current.Length,
                        Transform = new TranslateTransform(column * spacing, 0)
                    });
                    column++;
                }
            }
            control.SetValue(TextBlock.TextEffectsProperty, effects);
        }

        private void ApplyTextStyleToActiveBox()
        {
            if (_activeTextBox is null) return;
            _activeTextBox.Foreground = new SolidColorBrush(_textColor);
            _activeTextBox.Background = TextEditBackground();   // reflect the chosen fill live
            StyleEditBox(_activeTextBox);                       // typeface + B/I/S live
            int pg = _activeTextBox.Tag is int tp ? tp : PageList.SelectedIndex;
            double fontCanvas = _textFontSize;
            if (_doc is not null && pg >= 0 && _renderDims.TryGetValue(pg, out var rd) && rd.h > 0)
            {
                double sy = EnsureEngineDocumentSession().Pages[pg].Height / rd.h;
                if (sy > 0) fontCanvas = _textFontSize / sy;
            }
            _activeTextBox.FontSize = fontCanvas;
        }

        // Applies the current text style to whatever is active: the live edit box if one is open,
        // otherwise the selected text box (so its color / fill / size can be changed after placing it).
        // Opens the full RGB color picker seeded with the current color; applies the result on OK.
        private void OpenColorPicker(Color current, Action<Color> apply, Action? refreshBar = null)
        {
            var dlg = new ColorPickerDialog(this, Color.FromRgb(current.R, current.G, current.B));
            // Live-update the annotate bar behind the (modal) dialog whenever the shared palette is edited.
            if (refreshBar is not null) dlg.SwatchesChanged += refreshBar;
            dlg.ShowDialog();
            // dlg.Accepted, NEVER ShowDialog's return: the eyedropper's nested capture modal can
            // corrupt the outer dialog frame so ShowDialog returns false after a real OK, and the
            // pick was silently dropped - shapes then drew whatever color last got through
            // (the "purple/gray rectangles" bug, trace-proven 2026-08-01).
            if (dlg.Accepted) apply(dlg.SelectedColor);
        }

        // Diagonal rainbow fill for the "more colors" swatches that open the picker.
        private static LinearGradientBrush RainbowBrush() => new()
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(Colors.Red, 0), new GradientStop(Colors.Yellow, 0.25),
                new GradientStop(Colors.Lime, 0.5), new GradientStop(Colors.Cyan, 0.7),
                new GradientStop(Colors.Blue, 1)
            }
        };

        private void ApplyTextStyleToSelection()
        {
            if (_activeTextBox is not null)
            {
                ApplyTextStyleToActiveBox();
                return;
            }
            // Apply to the primary selection AND every shift-selected text annotation (these can span pages).
            var touched = new HashSet<int>();
            void Apply(TextAnnotation ta)
            {
                ta.SetColor(_textColor);
                ta.SetFill(_textFillColor);
                ta.FontName = _textFontName;
                ta.Bold = _textBold;
                ta.Italic = _textItalic;
                ta.Strike = _textStrike;
                ta.Underline = _textUnderline;
                ta.LetterSpacing = CanvasLetterSpacing(ta.PageIndex);
                double sy = 1.0;
                if (_doc is not null && _renderDims.TryGetValue(ta.PageIndex, out var rd) && rd.h > 0)
                    sy = EnsureEngineDocumentSession().Pages[ta.PageIndex].Height / rd.h;
                if (sy > 0 && _textFontSize > 0) ta.FontSize = _textFontSize / sy;
                touched.Add(ta.PageIndex);
            }
            if (_selectedAnnotation is TextAnnotation primary) Apply(primary);
            foreach (var a in _selectedSet)
                if (a is TextAnnotation ta && !ReferenceEquals(ta, _selectedAnnotation)) Apply(ta);
            if (touched.Count == 0) return;

            MarkDirty();
            int primPage = (_selectedAnnotation as TextAnnotation)?.PageIndex ?? -1;
            foreach (int p in touched) if (p != primPage) RenderAllAnnotations(p);
            if (primPage >= 0)
            {
                RenderAllAnnotations(primPage);   // render primary's page last so its chrome lands on _activeCanvas
                if (_selectionBorder is not null) _activeCanvas.Children.Add(_selectionBorder);
                foreach (var hd in _resizeHandles) _activeCanvas.Children.Add(hd);
            }
        }

        // Draw-bar counterpart to ApplyTextStyleToSelection: when a highlight / line / ink annotation
        // is selected (not just being freshly drawn), push the bar's current color, opacity and width
        // onto it and repaint - so editing an existing annotation works the same as setting up a new one.
        private void ApplyDrawStyleToSelection()
        {
            // Apply to the primary selection AND every shift-selected highlight / line / ink annotation.
            var touched = new HashSet<int>();
            void Apply(PageAnnotation a)
            {
                if (a is HighlightAnnotation ha)
                {
                    ha.SetColor(ha.Style == HighlightStyle.Fill ? _highlightColor : _lineAnnotColor);
                    touched.Add(ha.PageIndex);
                }
                else if (a is InkAnnotation ia)
                {
                    ia.SetColor(_drawColor);
                    ia.StrokeWidth = _drawWidth;
                    touched.Add(ia.PageIndex);
                }
            }
            if (_selectedAnnotation is not null) Apply(_selectedAnnotation);
            foreach (var a in _selectedSet)
                if (!ReferenceEquals(a, _selectedAnnotation)) Apply(a);
            if (touched.Count == 0) return;

            MarkDirty();
            int primPage = _selectedAnnotation?.PageIndex ?? -1;
            foreach (int p in touched) if (p != primPage) RenderAllAnnotations(p);
            if (primPage >= 0)
            {
                RenderAllAnnotations(primPage);
                ReattachSelectionVisuals();
            }
        }

        // TEXT SIDEBAR: a real column of the main window, sharing the left sidebar's theme.
        // In a normal window the outer right edge grows; when maximized, the document yields space.
        private bool _textSidebarCollapsed = true;
        private bool _textSidebarToggleWired;
        // SEMANTIC ANCHOR: TEXT_SIDEBAR_WIDTH_SSOT
        // Single source of truth for the shared right tool-pane width.
        // Keep it only as wide as needed for the widest unwrapped settings row (including
        // the font selector and all four style toggles); preserve consistent pane sizing
        // across tools instead of introducing tool-specific width overrides.
        // Expand/collapse and normal-window resizing must use this same value;
        // maximized/fullscreen windows keep their outer dimensions unchanged.
        private const double TextSidebarToolsWidth = 350;
        private const double TextSidebarRailWidth = 24;
        // SEMANTIC ANCHOR: TOOL_PANE_SSOT: Both text and annotation tools share this column, width, toggle, and resize policy. Never resize independently for a tool switch.
        private bool _textSidebarChangingWidth;

        private void SetTextSidebarCollapsed(bool collapsed)
        {
            if (_textSidebarChangingWidth) return;
            _textSidebarChangingWidth = true;
            try
            {
                bool wasExpanded = TextSidebarCol.Width.Value > TextSidebarRailWidth;
                bool requestedExpanded = TextSidebarToggle.Visibility == Visibility.Visible && !collapsed;
                // A format refresh must never resize the main window a second time.
                if (wasExpanded == requestedExpanded && _textSidebarCollapsed == collapsed)
                    return;
                LogTextSidebar("COLLAPSE_REQUEST", $"collapsed={collapsed}; previousWidth={TextSidebarCol.Width.Value}; windowWidth={Width}; state={WindowState}");
                bool willExpand = TextSidebarToggle.Visibility == Visibility.Visible && !collapsed;
                _textSidebarCollapsed = collapsed;
                TextSidebarBody.Visibility = willExpand ? Visibility.Visible : Visibility.Collapsed;
                TextSidebarCol.Width = new GridLength(TextSidebarRailWidth + (willExpand ? TextSidebarToolsWidth : 0));
                TextSidebarToggle.Content = collapsed ? "" : "";

                // Do not modify Left: the outer window grows to the right only.
                // Maximized and fullscreen windows keep their outer bounds unchanged.
                if (WindowState == WindowState.Normal && wasExpanded != willExpand)
                    Width = Math.Max(MinWidth, Width + (willExpand ? TextSidebarToolsWidth : -TextSidebarToolsWidth));
            }
            finally { _textSidebarChangingWidth = false; }
        }

        // TEXT SIDEBAR DIAGNOSTICS: no document contents or typed text are written.
        private string? _lastRenderedTextSettingsSignature;
        private static readonly string TextSidebarDebugPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PewDF", "text-sidebar-debug.log");

        private void LogTextSidebar(string eventName, string details)
        {
            if (!Services.DebugLogging.Enabled) return;
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(TextSidebarDebugPath)!);
                System.IO.File.AppendAllText(TextSidebarDebugPath,
                    $"{DateTimeOffset.Now:O} | {eventName} | {details}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Text sidebar diagnostics failed: {ex.Message}");
            }
        }

        // TEXT SIDEBAR DIAGNOSTICS: measure after WPF layout; never modify layout or scroll state.
        private void LogTextSidebarLayout(string trigger)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                if (TextSidebarContent.Children.Count != 1 || _textSettingsBar == null ||
                    !ReferenceEquals(TextSidebarContent.Children[0], _textSettingsBar)) return;

                var offsets = $"offsetX={TextSidebarScroll.HorizontalOffset:F2}; offsetY={TextSidebarScroll.VerticalOffset:F2}; " +
                    $"viewportW={TextSidebarScroll.ViewportWidth:F2}; extentW={TextSidebarScroll.ExtentWidth:F2}; " +
                    $"contentW={TextSidebarContent.ActualWidth:F2}; contentH={TextSidebarContent.ActualHeight:F2}";
                var positions = new System.Text.StringBuilder();
                if (_textSettingsBar.Child is StackPanel rows)
                {
                    for (int i = 0; i < rows.Children.Count; i++)
                    {
                        if (rows.Children[i] is not FrameworkElement row || !row.IsArrangeValid) continue;
                        Point point = row.TranslatePoint(new Point(0, 0), TextSidebarScroll);
                        positions.Append($"; row{i}=({point.X:F2},{point.Y:F2},{row.ActualWidth:F2},{row.ActualHeight:F2})");
                    }
                }
                // SEMANTIC ANCHOR: Diagnose only. Compare effective layout values and their
                // WPF value sources before/after refresh, without changing any UI properties.
                string Source(DependencyObject element, DependencyProperty property)
                {
                    var valueSource = DependencyPropertyHelper.GetValueSource(element, property);
                    return $"{valueSource.BaseValueSource}" +
                        (valueSource.IsAnimated ? "+Animated" : "") +
                        (valueSource.IsCoerced ? "+Coerced" : "");
                }
                string Describe(FrameworkElement element)
                {
                    string result = $"{element.GetType().Name}({element.Name})" +
                        $" size={element.ActualWidth:F2}x{element.ActualHeight:F2}" +
                        $" margin={element.Margin} [{Source(element, FrameworkElement.MarginProperty)}]" +
                        $" align={element.HorizontalAlignment}/{element.VerticalAlignment}" +
                        $" [{Source(element, FrameworkElement.HorizontalAlignmentProperty)}]";
                    if (element is Control control)
                        result += $" padding={control.Padding} [{Source(control, Control.PaddingProperty)}]";
                    if (element is Border border)
                        result += $" padding={border.Padding} [{Source(border, Border.PaddingProperty)}]" +
                            $" border={border.BorderThickness}";
                    return result;
                }
                var tree = new System.Text.StringBuilder();
                DependencyObject? current = _textSettingsBar;
                for (int depth = 0; current != null && depth < 12; depth++)
                {
                    if (current is FrameworkElement element)
                    {
                        Point origin = element.TranslatePoint(new Point(0, 0), TextSidebarScroll);
                        tree.Append($"; node{depth}={Describe(element)} origin=({origin.X:F2},{origin.Y:F2})");
                    }
                    if (ReferenceEquals(current, TextSidebarScroll)) break;
                    current = System.Windows.Media.VisualTreeHelper.GetParent(current);
                }
                tree.Append($"; scroll={Describe(TextSidebarScroll)}" +
                    $" content={Describe(TextSidebarContent)}");
                LogTextSidebar("LAYOUT", $"trigger={trigger}; {offsets}{positions}{tree}");
            }));
        }

        private string CurrentTextSettingsSignature() => string.Join("|",
            _textFontName, _textBold, _textItalic, _textStrike, _textUnderline,
            _textFontSize.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            _textLetterSpacing.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            _textColor.ToString(), _textFillColor.ToString(), _textOpacity.ToString());

        // TEXT SIDEBAR: WPF focus changes can request BringIntoView on the new editor/controls.
        // Such requests must not change the tool panel's scroll offset or apparent layout.
        // Wheel and scrollbar scrolling are intentionally unaffected.
        private void TextSidebarScroll_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            e.Handled = true;
        }

        private void ShowTextSettings()
        {
            string signature = CurrentTextSettingsSignature();
            bool alreadyVisible = _textSettingsBar != null &&
                TextSidebarContent.Children.Count == 1 &&
                ReferenceEquals(TextSidebarContent.Children[0], _textSettingsBar) &&
                TextSidebarToggle.Visibility == Visibility.Visible;
            if (alreadyVisible && signature == _lastRenderedTextSettingsSignature)
            {
                LogTextSidebar("REFRESH_SKIPPED", $"unchanged; expanded={!_textSidebarCollapsed}; width={TextSidebarCol.Width.Value}");
                LogTextSidebarLayout("refresh-skipped");
                return;
            }
            LogTextSidebar("REFRESH_START", $"visible={alreadyVisible}; expanded={!_textSidebarCollapsed}; width={TextSidebarCol.Width.Value}; settingsChanged={signature != _lastRenderedTextSettingsSignature}");
            bool appearing = _annotBarTool != EditTool.Text;   // real appear/switch vs same-tool refresh
            // Keep the currently displayed controls visible while the replacement is constructed.
            // Clearing here causes a visible empty frame whenever an existing text box is selected.

            // SEMANTIC ANCHOR: TEXT_TOOL_PANE_VISUAL_SSOT
            // Text uses shared pane metrics for recurring labels, swatches and sliders.
            // Preserve the existing text controls and actions; only visual metrics are shared.
            double PaneMetric(string key) => (double)FindResource(key);
            StackPanel Group()
            {
                return new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    // Right margin = the gap to the next group; top/bottom = the gap between wrapped rows.
                    Margin = new Thickness(0, 1, 16, 1)
                };
            }
            TextBlock DimLabel(string text, int top, bool rightAlign = false)
            {
                var t = new TextBlock
                {
                    Text = text,
                    FontFamily = UiKit.UiFont,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = rightAlign ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    Margin = new Thickness(0, top, PaneMetric("ToolPaneLabelControlGap"), 0)
                };
                t.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                return t;
            }
            // SEMANTIC ANCHOR: TOOL_PANE_COLOR_SELECTION_INVARIANT
            // Active swatches share SelectionAccent and the globally defined border metrics.
            Border ColorSwatch(Color c, bool isActive, MouseButtonEventHandler onClick)
            {
                var sw = new Border
                {
                    Width = PaneMetric("ToolPaneColorSwatchSize"),
                    Height = PaneMetric("ToolPaneColorSwatchSize"),
                    Background = new SolidColorBrush(c),
                    BorderThickness = new Thickness(PaneMetric(isActive ? "ToolPaneColorSwatchSelectedBorderThickness" : "ToolPaneColorSwatchIdleBorderThickness")),
                    CornerRadius = new CornerRadius(PaneMetric("ToolPaneColorSwatchCornerRadius")),
                    Margin = new Thickness(PaneMetric("ToolPaneColorSwatchMargin")),
                    Cursor = Cursors.Hand
                };
                if (isActive) sw.SetResourceReference(Border.BorderBrushProperty, "SelectionAccent");
                else sw.BorderBrush = _swatchDimBorder;
                sw.MouseLeftButtonDown += onClick;
                return sw;
            }
            // Opens the full RGB picker. When the current color isn't one of the presets it shows that
            // color with an accent ring (and a small rainbow corner), so the bar reflects a custom pick.
            Grid MoreColorsSwatch(Color current, bool customActive, MouseButtonEventHandler onClick)
            {
                var grid = new Grid { Width = PaneMetric("ToolPaneColorSwatchSize"), Height = PaneMetric("ToolPaneColorSwatchSize"), Margin = new Thickness(PaneMetric("ToolPaneColorSwatchMargin")), Cursor = Cursors.Hand, ToolTip = Loc("Str_Bar_MoreColors") };
                var bg = new Border
                {
                    CornerRadius = new CornerRadius(PaneMetric("ToolPaneColorSwatchCornerRadius")),
                    BorderThickness = new Thickness(PaneMetric(customActive ? "ToolPaneColorSwatchSelectedBorderThickness" : "ToolPaneColorSwatchIdleBorderThickness")),
                    Background = customActive
                        ? (Brush)new SolidColorBrush(Color.FromRgb(current.R, current.G, current.B))
                        : RainbowBrush()
                };
                if (customActive) bg.SetResourceReference(Border.BorderBrushProperty, "SelectionAccent"); else bg.BorderBrush = _swatchDimBorder;
                grid.Children.Add(bg);
                if (customActive)
                    grid.Children.Add(new System.Windows.Shapes.Polygon
                    {
                        Points = [new Point(PaneMetric("ToolPaneColorSwatchSize"), PaneMetric("ToolPaneColorSwatchSize") * 0.4), new Point(PaneMetric("ToolPaneColorSwatchSize"), PaneMetric("ToolPaneColorSwatchSize")), new Point(PaneMetric("ToolPaneColorSwatchSize") * 0.4, PaneMetric("ToolPaneColorSwatchSize"))],
                        Fill = RainbowBrush(),
                        IsHitTestVisible = false
                    });
                grid.MouseLeftButtonDown += onClick;
                return grid;
            }

            // Drag grip, centered vertically against whatever height the wrapped rows produce.
            var textGrip = MakeBarGrip(4);
            textGrip.VerticalAlignment = VerticalAlignment.Center;
            // (grip is added to the WrapPanel host directly, below.)

            var swatchRow1 = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = PaneMetric("ToolPaneSwatchRowMaxWidth") };
            foreach (var color in SwatchColors)
            {
                var c = color;
                bool isActive = c.R == _textColor.R && c.G == _textColor.G && c.B == _textColor.B;
                swatchRow1.Children.Add(ColorSwatch(c, isActive, (_, _) =>
                {
                    _textColor = Color.FromArgb(_textOpacity, c.R, c.G, c.B);
                    ApplyTextStyleToSelection();
                    ShowTextSettings();
                }));
            }
            bool textCustom = !SwatchColors.Any(sc => sc.R == _textColor.R && sc.G == _textColor.G && sc.B == _textColor.B);
            swatchRow1.Children.Add(MoreColorsSwatch(_textColor, textCustom, (_, _) => OpenColorPicker(_textColor, c =>
            {
                _textColor = Color.FromArgb(_textOpacity, c.R, c.G, c.B);
                ApplyTextStyleToSelection();
                ShowTextSettings();
            }, () => ShowTextSettings())));
            var grpColor = Group();
            grpColor.Orientation = Orientation.Vertical;
            grpColor.Children.Add(DimLabel(Loc("Str_Bar_Color"), 0));
            grpColor.Children.Add(swatchRow1);

            var swatchRow2 = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = PaneMetric("ToolPaneSwatchRowMaxWidth") };
            bool noneActive = _textFillColor.A == 0;
            var noneGrid = new Grid { Width = PaneMetric("ToolPaneColorSwatchSize"), Height = PaneMetric("ToolPaneColorSwatchSize"), Margin = new Thickness(PaneMetric("ToolPaneColorSwatchMargin")), Cursor = Cursors.Hand };
            var noneBg = new Border { CornerRadius = new CornerRadius(PaneMetric("ToolPaneColorSwatchCornerRadius")), Background = Brushes.White, BorderThickness = new Thickness(PaneMetric(noneActive ? "ToolPaneColorSwatchSelectedBorderThickness" : "ToolPaneColorSwatchIdleBorderThickness")) };
            if (noneActive) noneBg.SetResourceReference(Border.BorderBrushProperty, "SelectionAccent"); else noneBg.BorderBrush = _swatchDimBorder;
            noneGrid.Children.Add(noneBg);
            noneGrid.Children.Add(new System.Windows.Shapes.Line { X1 = PaneMetric("ToolPaneColorSwatchSize") * 0.17, Y1 = PaneMetric("ToolPaneColorSwatchSize") * 0.83, X2 = PaneMetric("ToolPaneColorSwatchSize") * 0.83, Y2 = PaneMetric("ToolPaneColorSwatchSize") * 0.17, Stroke = Brushes.Red, StrokeThickness = 1.5 });
            noneGrid.MouseLeftButtonDown += (_, _) =>
            {
                _textFillColor = Color.FromArgb(0, _textFillColor.R, _textFillColor.G, _textFillColor.B);
                ApplyTextStyleToSelection();
                ShowTextSettings();
            };
            swatchRow2.Children.Add(noneGrid);
            foreach (var color in SwatchColors)
            {
                var c = color;
                bool isActive = _textFillColor.A > 0 && c.R == _textFillColor.R && c.G == _textFillColor.G && c.B == _textFillColor.B;
                swatchRow2.Children.Add(ColorSwatch(c, isActive, (_, _) =>
                {
                    byte a = _textFillColor.A == 0 ? (byte)255 : _textFillColor.A;   // enable at full/current opacity
                    _textFillColor = Color.FromArgb(a, c.R, c.G, c.B);
                    ApplyTextStyleToSelection();
                    ShowTextSettings();
                }));
            }
            bool fillCustom = _textFillColor.A > 0 && !SwatchColors.Any(sc => sc.R == _textFillColor.R && sc.G == _textFillColor.G && sc.B == _textFillColor.B);
            swatchRow2.Children.Add(MoreColorsSwatch(_textFillColor.A == 0 ? Colors.White : _textFillColor, fillCustom, (_, _) => OpenColorPicker(_textFillColor.A == 0 ? Colors.White : _textFillColor, c =>
            {
                byte a = _textFillColor.A == 0 ? (byte)255 : _textFillColor.A;
                _textFillColor = Color.FromArgb(a, c.R, c.G, c.B);
                ApplyTextStyleToSelection();
                ShowTextSettings();
            }, () => ShowTextSettings())));
            var grpFill = Group();
            grpFill.Orientation = Orientation.Vertical;
            grpFill.Children.Add(DimLabel(Loc("Str_Bar_Fill"), 0));
            grpFill.Children.Add(swatchRow2);

            // SEMANTIC ANCHOR: TEXT_TOOL_NUMERIC_SSOT
            // Font size and letter spacing use the shared tool-pane numeric field and stepper.
            // Persisted text properties remain authoritative; preserve each control's original
            // permitted range and step size independently of visual metrics.
            StackPanel NumericSetting(string label, Func<double> current, Action<double> apply,
                double min, double max, double step)
            {
                var group = new StackPanel { Orientation = Orientation.Vertical,
                    Margin = new Thickness(0, 1, 16, 1) };
                group.Children.Add(DimLabel(label, 0));
                var row = new StackPanel { Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, PaneMetric("ToolPaneLabelControlGap"), 0, 0) };
                var box = UiKit.Field(PaneMetric("ToolPaneNumericFieldWidth"));
                box.Height = PaneMetric("ToolPaneNumericFieldHeight");
                box.MaxLength = 8;
                box.TextAlignment = TextAlignment.Center;
                box.VerticalContentAlignment = VerticalAlignment.Center;
                box.Padding = new Thickness(3, 0, 3, 0);
                box.Text = $"{current():0.##}";
                box.SetResourceReference(TextBox.BackgroundProperty, "TextFieldBrush");
                box.SetResourceReference(TextBox.ForegroundProperty, "TextBrush");
                box.SetResourceReference(TextBox.BorderBrushProperty, "CardBorderBrush");
                box.SetResourceReference(TextBox.CaretBrushProperty, "PrimaryBrush");
                box.SetResourceReference(TextBox.SelectionBrushProperty, "RowSelectedBrush");
                void SetValue(double value)
                {
                    if (!double.IsFinite(value)) return;
                    apply(Math.Clamp(Math.Round(value, 2), min, max));
                    box.Text = $"{current():0.##}";
                    ApplyTextStyleToSelection();
                }
                void Commit()
                {
                    if (double.TryParse(box.Text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.CurrentCulture, out double value) && double.IsFinite(value))
                        SetValue(value);
                    else
                        box.Text = $"{current():0.##}";
                }
                box.LostKeyboardFocus += (_, _) => Commit();
                box.PreviewKeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
                    else if (e.Key == Key.Escape) { box.Text = $"{current():0.##}"; e.Handled = true; }
                    else if (e.Key == Key.Up || e.Key == Key.Down)
                    {
                        Commit();
                        SetValue(current() + (e.Key == Key.Up ? step : -step));
                        e.Handled = true;
                    }
                };
                box.GotKeyboardFocus += (_, _) => box.SelectAll();
                row.Children.Add(box);
                var spinner = new Grid
                {
                    Width = PaneMetric("ToolPaneNumericStepperWidth"),
                    Height = PaneMetric("ToolPaneNumericFieldHeight"),
                    Margin = new Thickness(PaneMetric("ToolPaneNumericStepperGap"), 0, 0, 0)
                };
                spinner.RowDefinitions.Add(new RowDefinition());
                spinner.RowDefinitions.Add(new RowDefinition());
                System.Windows.Controls.Primitives.RepeatButton StepButton(string glyph, double delta, int rowIndex)
                {
                    var button = new System.Windows.Controls.Primitives.RepeatButton
                    {
                        Content = glyph,
                        FontSize = PaneMetric("ToolPaneNumericStepperGlyphSize"),
                        Padding = new Thickness(0),
                        Style = (Style)FindResource("ToolPaneNumericStepButton"),
                        Delay = 350, Interval = 100, Cursor = Cursors.Hand
                    };
                    button.Click += (_, _) => { Commit(); SetValue(current() + delta); };
                    Grid.SetRow(button, rowIndex);
                    return button;
                }
                spinner.Children.Add(StepButton("▲", step, 0));
                spinner.Children.Add(StepButton("▼", -step, 1));
                row.Children.Add(spinner);
                var unit = DimLabel("pt", 0);
                unit.Margin = new Thickness(PaneMetric("ToolPaneLabelControlGap"), 0, 0, 0);
                row.Children.Add(unit);
                group.Children.Add(row);
                return group;
            }
            var sizeStack = NumericSetting(Loc("Str_Bar_Size"), () => _textFontSize,
                value => _textFontSize = value, 1, 400, 1);
            var spacingStack = NumericSetting(Loc("Str_Bar_LetterSpacing"), () => _textLetterSpacing,
                value => _textLetterSpacing = value, -2, 20, 0.25);

            // Font group: typeface selector + Bold / Italic / Strikethrough / Underline.
            // A small square toggle whose glyph previews its own effect (bold B, italic I, struck-through S).
            Border StyleToggle(string glyph, string tip, bool active, FontWeight fw, FontStyle fs, TextDecorationCollection? deco, Action onClick)
            {
                var gt = new TextBlock
                {
                    Text = glyph,
                    FontFamily = UiKit.UiFont,
                    FontSize = 12,
                    FontWeight = fw,
                    FontStyle = fs,
                    TextDecorations = deco,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                gt.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                var b = new Border
                {
                    Width = 22,
                    Height = 20,
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(2, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = tip,
                    BorderThickness = new Thickness(active ? 2 : 1),
                    Background = active ? AccentBrush(40) : Brushes.Transparent,
                    Child = gt
                };
                if (active) b.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush"); else b.BorderBrush = _swatchDimBorder;
                b.MouseLeftButtonDown += (_, _) => onClick();
                return b;
            }

            var fontStack = Group();
            fontStack.Children.Add(DimLabel(Loc("Str_Bar_Font"), 0));
            var fontBox = new ComboBox
            {
                Width = 132,
                Height = 22,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = UiKit.UiFont,
                FontSize = 11,
                MaxDropDownHeight = 320,
                Margin = new Thickness(0, 0, 4, 0)
            };
            if (FindResource("DarkComboBox") is Style cbStyle) fontBox.Style = cbStyle;
            if (FindResource("DarkComboItem") is Style ciStyle) fontBox.ItemContainerStyle = ciStyle;
            foreach (var fn in SystemFontNames) fontBox.Items.Add(fn);
            fontBox.SelectedItem = _textFontName;
            fontBox.SelectionChanged += (s, e) =>
            {
                if (fontBox.SelectedItem is string fn) { _textFontName = fn; ApplyTextStyleToSelection(); }
            };
            fontStack.Children.Add(fontBox);
            fontStack.Children.Add(StyleToggle("B", Loc("Str_Lbl_Bold"), _textBold, FontWeights.Bold, FontStyles.Normal, null,
                () => { _textBold = !_textBold; ApplyTextStyleToSelection(); ShowTextSettings(); }));
            fontStack.Children.Add(StyleToggle("I", Loc("Str_Lbl_Italic"), _textItalic, FontWeights.Normal, FontStyles.Italic, null,
                () => { _textItalic = !_textItalic; ApplyTextStyleToSelection(); ShowTextSettings(); }));
            fontStack.Children.Add(StyleToggle("S", Loc("Str_Lbl_Strike"), _textStrike, FontWeights.Normal, FontStyles.Normal, TextDecorations.Strikethrough,
                () => { _textStrike = !_textStrike; ApplyTextStyleToSelection(); ShowTextSettings(); }));
            fontStack.Children.Add(StyleToggle("U", Loc("Str_Lbl_Underline"), _textUnderline, FontWeights.Normal, FontStyles.Normal, TextDecorations.Underline,
                () => { _textUnderline = !_textUnderline; ApplyTextStyleToSelection(); ShowTextSettings(); }));

            // Opacity and Fill Opacity: two independent single-row groups.
            var grpOpacity = Group();
            grpOpacity.Children.Add(DimLabel(Loc("Str_Bar_Opacity"), 0));
            var opacitySlider = new Slider
            {
                Minimum = 10,
                Maximum = 255,
                Value = _textOpacity,
                Width = PaneMetric("ToolPaneControlSliderWidth"),
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("DarkSlider")
            };
            grpOpacity.Children.Add(opacitySlider);
            var opacityLabel = new TextBlock
            {
                Text = $"{(int)(_textOpacity / 255.0 * 100)}%",
                FontFamily = UiKit.UiFont,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(PaneMetric("ToolPaneOpacityValueGap"), 0, 0, 0),
                Width = PaneMetric("ToolPaneOpacityValueWidth"),
                TextAlignment = TextAlignment.Right
            };
            opacityLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            grpOpacity.Children.Add(opacityLabel);
            opacitySlider.ValueChanged += (s, e) =>
            {
                byte a = (byte)e.NewValue;
                opacityLabel.Text = $"{(int)(a / 255.0 * 100)}%";
                _textOpacity = a;
                _textColor = Color.FromArgb(a, _textColor.R, _textColor.G, _textColor.B);
                ApplyTextStyleToSelection();
            };

            var grpFillOp = Group();
            grpFillOp.Children.Add(DimLabel(Loc("Str_Bar_FillOpacity"), 0));
            byte curFillA = _textFillColor.A == 0 ? (byte)255 : _textFillColor.A;
            var fillOpSlider = new Slider
            {
                Minimum = 10,
                Maximum = 255,
                Value = curFillA,
                Width = PaneMetric("ToolPaneControlSliderWidth"),
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("DarkSlider")
            };
            grpFillOp.Children.Add(fillOpSlider);
            var fillOpLabel = new TextBlock
            {
                Text = $"{(int)(curFillA / 255.0 * 100)}%",
                FontFamily = UiKit.UiFont,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(PaneMetric("ToolPaneOpacityValueGap"), 0, 0, 0),
                Width = PaneMetric("ToolPaneOpacityValueWidth"),
                TextAlignment = TextAlignment.Right
            };
            fillOpLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            grpFillOp.Children.Add(fillOpLabel);
            fillOpSlider.ValueChanged += (s, e) =>
            {
                byte a = (byte)e.NewValue;
                fillOpLabel.Text = $"{(int)(a / 255.0 * 100)}%";
                // Dragging opacity turns the fill on (defaults to the current color, white for whiteout).
                _textFillColor = Color.FromArgb(a, _textFillColor.R, _textFillColor.G, _textFillColor.B);
                ApplyTextStyleToSelection();
            };

            // Vertical order requested for the right-hand tool sidebar.
            var contentStack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Left };
            // SEMANTIC ANCHOR: TOOL_PANE_SECTION_GAP_INVARIANT
            // Section spacing is app-scoped SSOT; inline control margins remain unchanged.
            double sectionGap = (double)FindResource("ToolPaneSectionGap");
            var textSections = new FrameworkElement[]
            {
                fontStack, sizeStack, spacingStack, grpColor, grpFill, grpOpacity, grpFillOp
            };
            for (int i = 0; i < textSections.Length; i++)
            {
                if (i > 0) textSections[i].Margin = new Thickness(0, sectionGap, 0, 0);
                contentStack.Children.Add(textSections[i]);
            }
            _annotBarDragInners.Clear();

            _textSettingsBar = new Border
            {
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Padding = new Thickness(0),
                Child = contentStack
            };
            _textSettingsBar.SetResourceReference(Border.BackgroundProperty, "BackgroundBrush");
            // Swap the completed bar in one synchronous UI operation instead of exposing an
            // empty sidebar for the whole control-construction phase.
            TextSidebarContent.Children.Clear();
            TextSidebarContent.HorizontalAlignment = HorizontalAlignment.Left;
            TextSidebarContent.Children.Add(_textSettingsBar);
            ToolSidebarHeader.SetResourceReference(TextBlock.TextProperty, "Str_Ribbon_Text");
            // Reopening an editor must not inherit a shifted horizontal viewport.
            // Keep the content anchored to the document-side edge of the sidebar.
            TextSidebarScroll.ScrollToHorizontalOffset(0);
            TextSidebarScroll.ScrollToVerticalOffset(0);
            _lastRenderedTextSettingsSignature = CurrentTextSettingsSignature();
            LogTextSidebar("REFRESH_SWAP", $"controls={TextSidebarContent.Children.Count}; width={TextSidebarCol.Width.Value}");
            if (!_textSidebarToggleWired)
            {
                TextSidebarToggle.Click += (_, _) => SetTextSidebarCollapsed(!_textSidebarCollapsed);
                TextSidebarScroll.ScrollChanged += (_, e) =>
                {
                    if (e.HorizontalChange != 0 || e.VerticalChange != 0 ||
                        e.ViewportWidthChange != 0 || e.ExtentWidthChange != 0)
                        LogTextSidebarLayout("scroll-changed");
                };
                _textSidebarToggleWired = true;
            }
            TextSidebarToggle.Visibility = Visibility.Visible;
            SetTextSidebarCollapsed(appearing ? false : _textSidebarCollapsed);
            _annotBarTool = EditTool.Text;
            _annotBarMinimized = false;   // a freshly built bar is full-size
            LogTextSidebarLayout("refresh-swap");
        }

        // (The shared overflow machinery - MakeBarOverflow and WireBarOverflow - lives in
        // AnnotationBars.cs, used by this bar and the draw/highlight/line/shape bar alike.)

        // SEMANTIC ANCHOR: TOOL_PANE_OWNERSHIP_INVARIANT: Only the displayed owner may close
        // the shared pane. Replacing its contents must not shrink/re-expand the outer window.
        private void HideToolPaneContent(Border? owner, bool keepPane = false)
        {
            if (owner is null || TextSidebarContent.Children.Count != 1 ||
                !ReferenceEquals(TextSidebarContent.Children[0], owner)) return;
            TextSidebarContent.Children.Clear();
            if (keepPane) return; // replacing a tool retains the existing window geometry
            if (TextSidebarCol.Width.Value > TextSidebarRailWidth && WindowState == WindowState.Normal)
                Width = Math.Max(MinWidth, Width - TextSidebarToolsWidth);
            _textSidebarCollapsed = true;
            TextSidebarBody.Visibility = Visibility.Collapsed;
            TextSidebarToggle.Visibility = Visibility.Collapsed;
            TextSidebarCol.Width = new GridLength(TextSidebarRailWidth);
        }

        private void HideTextSettings(bool keepPane = false)
        {
            LogTextSidebar("HIDE", $"expanded={!_textSidebarCollapsed}; width={TextSidebarCol.Width.Value}");
            _lastRenderedTextSettingsSignature = null;
            // The pane is shared: closing Text must not dispose a different tool's contents.
            HideToolPaneContent(_textSettingsBar, keepPane);
            _textSettingsBar = null;
            if (_annotBarTool == EditTool.Text) _annotBarTool = null;
        }

        private void PlaceImageFromDialog(Point pos, int pageIdx)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog()
            {
                Title = Loc("Str_Dlg_InsertImage"),
                Filter = Loc("Str_Filter_Images") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff;*.tif|" + Loc("Str_Filter_AllFiles") + "|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                var imgBytes = File.ReadAllBytes(dlg.FileName);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(imgBytes);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();

                double srcW = bmp.PixelWidth > 0 ? bmp.PixelWidth : 400;
                double srcH = bmp.PixelHeight > 0 ? bmp.PixelHeight : 300;

                // Default the placed image to ~50% of the page's longest side (in render-dim
                // units) so it is a usable size regardless of page dimensions, never upscaling
                // beyond the source's native resolution.
                double pageMax = _renderDims.TryGetValue(pageIdx, out var rdImg)
                    ? Math.Max(rdImg.w, rdImg.h) : 2048.0;
                double MaxCanvasDim = pageMax * 0.5;
                double scale = Math.Min(1.0, Math.Min(MaxCanvasDim / srcW, MaxCanvasDim / srcH));

                var imgAnnot = new ImageAnnotation
                {
                    PageIndex = pageIdx,
                    Position = pos,
                    Scale = scale,
                    SourceWidth = srcW,
                    SourceHeight = srcH,
                    ImageData = Convert.ToBase64String(imgBytes)
                };

                // Switch to Select FIRST so placement renders last and nothing wipes the image
                // (calling SetTool between render and select was what made the image vanish).
                SetTool(EditTool.Select);
                AddAnnotation(imgAnnot);
                RenderAllAnnotations(pageIdx);
                double w = srcW * scale;
                double h = srcH * scale;
                SelectAnnotation(imgAnnot, new Rect(pos.X, pos.Y, w, h));
                SetStatus(Loc("Str_St_ImagePlaced"));
            }
            catch (Exception ex)
            {
                PewDialog.Show(this, Loc("Str_Err_LoadImageFailed") + "\n" + ex.Message, "PewDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Ctrl+V: drop a clipboard image (as an image annotation) or clipboard text (as a text
        // annotation) onto the current page, centered, then select it. Coordinates are in the page's
        // render-dim space (== _renderDims[page]), matching how clicks place annotations.
        private void PasteFromClipboard()
        {
            if (_doc is null) return;
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) pageIdx = 0;
            if (pageIdx >= EnsureEngineDocumentSession().PageCount) return;

            double pw = _renderDims.TryGetValue(pageIdx, out var rd) ? rd.w : 2048.0;
            double ph = _renderDims.TryGetValue(pageIdx, out var rd2) ? rd2.h : 2048.0;

            try
            {
                if (Clipboard.ContainsImage())
                {
                    var src = Clipboard.GetImage();
                    if (src is null) { SetStatus(Loc("Str_St_ClipImageUnreadable")); return; }

                    byte[] imgBytes = BitmapHelpers.EncodeClipboardImagePng(src);

                    double srcW = src.PixelWidth > 0 ? src.PixelWidth : 400;
                    double srcH = src.PixelHeight > 0 ? src.PixelHeight : 300;
                    double pageMax = Math.Max(pw, ph);
                    double maxCanvasDim = pageMax * 0.5;
                    double scale = Math.Min(1.0, Math.Min(maxCanvasDim / srcW, maxCanvasDim / srcH));
                    double w = srcW * scale, h = srcH * scale;
                    var pos = new Point((pw - w) / 2, (ph - h) / 2);

                    var imgAnnot = new ImageAnnotation
                    {
                        PageIndex = pageIdx,
                        Position = pos,
                        Scale = scale,
                        SourceWidth = srcW,
                        SourceHeight = srcH,
                        ImageData = Convert.ToBase64String(imgBytes)
                    };
                    SetTool(EditTool.Select);
                    AddAnnotation(imgAnnot);
                    RenderAllAnnotations(pageIdx);
                    SelectAnnotation(imgAnnot, new Rect(pos.X, pos.Y, w, h));
                    SetStatus(Loc("Str_St_PastedImage"));
                }
                else if (Clipboard.ContainsText())
                {
                    string content = Clipboard.GetText().Trim();
                    if (string.IsNullOrEmpty(content)) { SetStatus(Loc("Str_St_ClipNoText")); return; }

                    // Convert the point size to the page's canvas units (see PlaceTextBox).
                    double fontCanvas = _textFontSize;
                    double sy = EnsureEngineDocumentSession().Pages[pageIdx].Height / Math.Max(1.0, ph);
                    if (sy > 0) fontCanvas = _textFontSize / sy;

                    var ta = new TextAnnotation
                    {
                        PageIndex = pageIdx,
                        Position = new Point(pw * 0.25, ph * 0.45),
                        Content = content,
                        FontSize = fontCanvas,
                        LetterSpacing = CanvasLetterSpacing(pageIdx)
                    };
                    ta.SetColor(_textColor);
                    SetTool(EditTool.Select);
                    AddAnnotation(ta);
                    RenderAllAnnotations(pageIdx);
                    SelectAnnotation(ta, AnnotBounds(ta));
                    SetStatus(Loc("Str_St_PastedText"));
                }
                else
                {
                    SetStatus(Loc("Str_St_ClipEmpty"));
                }
            }
            catch (Exception ex)
            {
                PewDialog.Show(this, Loc("Str_Err_PasteFailed") + "\n" + ex.Message, "PewDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

    }
}
