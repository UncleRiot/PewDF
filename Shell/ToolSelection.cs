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
        // Tool selection
        // ============================================================

        // Maps an editing tool to its mouse cursor. Shared by SetTool and by the
        // per-page overlay creation so freshly rendered tiles get the right cursor.
        internal static Cursor CursorForTool(EditTool tool) => tool switch
        {
            EditTool.Text => Cursors.IBeam,
            EditTool.FormField => Cursors.Cross,
            // SEMANTIC ANCHOR: TEXT_HIGHLIGHT_CURSOR_INVARIANT
            // Text-bound highlighting uses the text-selection cursor; CursorForTool
            // remains the single source for both primary and continuous page overlays.
            EditTool.Highlight => Cursors.IBeam,
            EditTool.Strikethrough => Cursors.Cross,
            EditTool.Underline => Cursors.Cross,
            EditTool.Draw => Cursors.Pen,
            // SEMANTIC ANCHOR: TEXT_MARKER_CURSOR_INVARIANT
            // The cursor hotspot sits on the downward-facing chisel tip; the body runs down/right.
            // This central mapping is used by primary and continuous-view canvases alike.
            EditTool.TextMarker => DynamicTextMarkerCursor ?? Cursors.Cross,
            EditTool.Line => Cursors.Cross,
            EditTool.Shape => Cursors.Cross,
            EditTool.Signature => Cursors.Arrow,
            EditTool.Image => Cursors.Hand,
            EditTool.Crop => Cursors.Cross,
            EditTool.Rotate => Cursors.Cross,
            EditTool.Measure => Cursors.Cross,
            _ => Cursors.Arrow
        };

        // SEMANTIC ANCHOR: TEXT_MARKER_CURSOR_FALLBACK_INVARIANT
        // The dynamically generated cursor is the only marker cursor resource.
        // Before it is available or if creation fails, use the built-in cross cursor;
        // do not reference removed packaged CUR files.

        // SEMANTIC ANCHOR: TEXT_MARKER_CURSOR_SETTINGS_SSOT
        // Cursor color, nib width and angle are derived from the active viewer, never cached separately.
        private static Cursor? DynamicTextMarkerCursor;
        private void RefreshTextMarkerCursor()
        {
            if (_currentTool != EditTool.TextMarker) return;
            var viewer = ActiveViewer;
            try
            {
                DynamicTextMarkerCursor = CreateTextMarkerCursor(viewer.TextMarkerColor, viewer.TextMarkerNibSize, viewer.TextMarkerNibAngle);
                var cursor = CursorForTool(EditTool.TextMarker);
                _annotationCanvas.Cursor = cursor;
                foreach (var canvas in _continuousCanvases.Values) canvas.Cursor = cursor;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Text marker cursor unavailable: {ex.Message}");
                DynamicTextMarkerCursor = Cursors.Cross;
            }
        }

        private static Cursor CreateTextMarkerCursor(Color color, double nibSize, double nibAngle)
        {
            // SEMANTIC ANCHOR: TEXT_MARKER_APPROVED_IMAGE_SSOT
            // The approved original silhouette is the packaged image, not recreated geometry.
            // Color is derived from active tool settings; aspect ratio must never change.
            // Tip hotspot is the image's upper-left nib contact, never the barrel centre.
            // nibAngle intentionally does not rotate the approved cursor artwork.
            _ = nibAngle;
            const int canvasSize = 128;
            const int hotspotX = 12;
            const int hotspotY = 20;
            double scale = Math.Clamp(nibSize / 20.0, 0.40, 1.25) * 0.66;

            var resource = Application.GetResourceStream(
                new Uri("pack://application:,,,/Resources/textmarker-approved.png"))
                ?? throw new InvalidOperationException("Approved textmarker image resource is missing.");
            BitmapSource original;
            using (resource.Stream)
            {
                var decoder = BitmapDecoder.Create(resource.Stream, BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                original = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            }
            int stride = original.PixelWidth * 4;
            var pixels = new byte[stride * original.PixelHeight];
            original.CopyPixels(pixels, stride, 0);
            // Recolor only pink pigment (barrel and nib); leave original neck and outlines intact.
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                if (r > 110 && r > g * 1.25 && b > g * 1.12 && r >= b)
                {
                    double shade = Math.Clamp((r + g + b) / (252.0 + 112.0 + 169.0), 0.48, 1.17);
                    pixels[i] = (byte)Math.Clamp((int)Math.Round(color.B * shade), 0, 255);
                    pixels[i + 1] = (byte)Math.Clamp((int)Math.Round(color.G * shade), 0, 255);
                    pixels[i + 2] = (byte)Math.Clamp((int)Math.Round(color.R * shade), 0, 255);
                }
            }
            var tinted = BitmapSource.Create(original.PixelWidth, original.PixelHeight,
                96, 96, PixelFormats.Bgra32, null, pixels, stride);
            tinted.Freeze();
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                // Template nib contact: pixel (2,10) within the preserved crop.
                dc.DrawImage(tinted, new Rect(hotspotX - 2 * scale,
                    hotspotY - 10 * scale, tinted.PixelWidth * scale, tinted.PixelHeight * scale));
            }
            var bitmap = new RenderTargetBitmap(canvasSize, canvasSize, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var png = new MemoryStream();
            encoder.Save(png);
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write((ushort)0); writer.Write((ushort)2); writer.Write((ushort)1);
                writer.Write((byte)canvasSize); writer.Write((byte)canvasSize);
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)hotspotX); writer.Write((ushort)hotspotY);
                writer.Write((uint)png.Length); writer.Write((uint)22);
                writer.Write(png.ToArray());
            }
            stream.Position = 0;
            return new Cursor(stream);
        }

        // SEMANTIC ANCHOR: CROP_PANE_SINGLE_OWNER_INVARIANT
        // Crop creates the controls in PdfViewer, but only this shared pane owns their
        // layout, visibility and resize policy. Never collapse a replacement tool's pane.
        private void OnCropPaneChanged(Controls.PdfViewer source, Border? bar)
        {
            if (bar is null)
            {
                if (_cropPaneOwner is not null &&
                    TextSidebarContent.Children.Contains(_cropPaneOwner))
                    HideToolPaneContent(_cropPaneOwner);
                _cropPaneOwner = null;
                return;
            }
            if (!ReferenceEquals(source, ActiveViewer)) return;
            _cropPaneOwner = bar;
            TextSidebarContent.Children.Clear();
            TextSidebarContent.Children.Add(bar);
            ToolSidebarHeader.SetResourceReference(TextBlock.TextProperty, "Str_Crop_Apply");
            if (!_textSidebarToggleWired)
            {
                TextSidebarToggle.Click += (_, _) => SetTextSidebarCollapsed(!_textSidebarCollapsed);
                _textSidebarToggleWired = true;
            }
            TextSidebarToggle.Visibility = Visibility.Visible;
            SetTextSidebarCollapsed(false);
            _annotBarTool = EditTool.Crop;
        }

        private Border? _cropPaneOwner;

        private void SetTool(EditTool tool, bool restoringPane = false)
        {
            // Re-clicking the tool that owns the visible annotate bar tucks the bar away (or brings it
            // back) instead of rebuilding it - no flicker, and a quick way to get it out of the way.
            bool reclickedAnnotTool = !restoringPane && tool == _currentTool && tool == _annotBarTool
                && (_textSettingsBar is not null || _drawSettingsBar is not null || _cropConfirmBar is not null || _textMarkerSettingsBar is not null);
            if (reclickedAnnotTool)
            {
                // SEMANTIC ANCHOR: TOOL_PANE_TOGGLE_INVARIANT: Sidebar tools use the
                // single pane toggle, never the legacy floating-bar height animation.
                if (tool is EditTool.Text or EditTool.Draw or EditTool.Line or EditTool.Shape
                    or EditTool.Highlight or EditTool.Strikethrough or EditTool.Underline or EditTool.Crop or EditTool.TextMarker)
                    SetTextSidebarCollapsed(!_textSidebarCollapsed);
                else
                    ToggleAnnotBarMinimized();
                return;
            }

            // Continuous view now supports annotation tools inline via per-page overlays.
            CommitActiveTextBox();
            ClearTextSelection();
            CancelShapePolygon();   // abandon an in-progress polygon when switching tools
            if (tool != EditTool.Draw) HideBrushPreview();   // drop the brush cursor when leaving Draw
            if (tool != EditTool.Measure) ActiveViewer.ClearMeasurementExt();
            _currentTool = tool;
            ActiveViewer.RefreshFormDesignMode();

            // SEMANTIC ANCHOR: TOOL_STATE_SINGLE_OWNER_INVARIANT.
            // _currentTool owns the selection state. The active Home ribbon renders
            // it through UpdateRibbonPreviewSelection; no hidden toolbar is consulted.

            // Keep the new ribbon's selected-state visuals synchronized with the existing
            // tool mode. Do not introduce any parallel selection state.
            UpdateRibbonPreviewSelection();
            RefreshFormRibbonButtons();

            // Apply the tool cursor to every page surface, not just the primary page.
            // In Grid / Two-Page / Continuous modes the secondary tiles are separate
            // overlay canvases tracked in _continuousCanvases; without this they keep
            // the default arrow cursor while only page 1 (_annotationCanvas) updates.
            var toolCursor = CursorForTool(tool);
            _annotationCanvas.Cursor = toolCursor;
            foreach (var overlay in _continuousCanvases.Values)
                overlay.Cursor = toolCursor;

            // SEMANTIC ANCHOR: TOOL_PANE_SWITCH_INVARIANT: Release the previous owner's
            // content before installing the new tool. A draw-to-text switch must not leave
            // floating controls or shrink the newly installed pane.
            bool drawTool = tool is EditTool.Draw or EditTool.Line or EditTool.Shape
                or EditTool.Highlight or EditTool.Strikethrough or EditTool.Underline;
            // SEMANTIC ANCHOR: TEXT_MARKER_PANE_RELEASE_INVARIANT
            // Clear marker-owned content when another tool takes ownership; retain pane width.
            if (tool != EditTool.TextMarker)
                HideTextMarkerSettings(keepPane: drawTool || tool == EditTool.Text || tool == EditTool.Crop || tool == EditTool.Signature);
            if (drawTool)
            {
                HideTextSettings(keepPane: true);
                ShowDrawSettings(tool);
            }
            else if (tool == EditTool.TextMarker)
            {
                HideDrawSettings(keepPane: true);
                HideTextSettings(keepPane: true);
                ShowTextMarkerSettings();
                RefreshTextMarkerCursor();
            }
            else if (tool == EditTool.Text)
            {
                HideDrawSettings(keepPane: true);
                ShowTextSettings();
            }
            else
            {
                // SEMANTIC ANCHOR: SIGNATURE_TOOL_SWITCH_INVARIANT
                // Preserve the column while replacing sidebar content with Signature.
                // Its owner is released only when leaving Signature for a non-pane tool.
                HideDrawSettings(keepPane: tool is EditTool.Signature or EditTool.Crop);
                HideTextSettings(keepPane: tool is EditTool.Signature or EditTool.Crop);
            }

            // Hide signature popup when switching away
            if (tool != EditTool.Signature)
            {
                HideSignaturePopup();
                _pendingSignature = null;
            }

            // Dismiss crop confirm bar when switching away from Crop; entering Crop drops a default box + bar.
            if (tool != EditTool.Crop)
                HideCropConfirmBar();
            else if (_cropConfirmBar is null)
                ShowDefaultCropBox();

            // NOTE: deliberately NOT reflowing the toolbar here. Reflowing on every tool switch at a narrow
            // width visibly thrashes the whole bar. The active-tool protection still runs on resize (the
            // next time the window changes width the active tool is pulled back onto the bar), which the
            // user preferred over the jank.
        }

        private void SidebarToggle_Click(object sender, RoutedEventArgs e)
        {
            // A toggle from the USER makes the state deliberate: a closed sidebar then stays
            // closed until the user opens it - SyncSidebarToDocState may only undo its own
            // automatic collapse (see _sidebarAutoCollapsed).
            if (!_sidebarAutoToggling) _sidebarAutoCollapsed = false;
            _sidebarCollapsed = !_sidebarCollapsed;
            if (_sidebarCollapsed)
            {
                // Save current width before collapsing so expand restores it.
                if (_sidebarCol.ActualWidth > 24)
                {
                    if (_sidebarShowingOutlines)
                        _savedOutlinesWidth = Math.Min(_sidebarCol.ActualWidth, SbPx(SidebarMaxOutlines));
                    else
                        _savedPagesWidth = Math.Min(_sidebarCol.ActualWidth, SbPx(SidebarMaxPages));
                }
                _sidebarToggleBtn.ToolTip = Loc("Str_TT_ExpandSidebar");
                // Glide shut sliding, not squishing: freeze the content at its open width so
                // the shrinking border CLIPS it (thumbnails hold their size), then hide the
                // border once the strip width is reached. MinWidth drops first so the
                // animation isn't clamped at the readable floor.
                BeginSidebarSlide(SidebarContentPanel.ActualWidth);
                _sidebarCol.MinWidth = SbPx(24);
                AnimateSidebarWidth(SbPx(24), () =>
                {
                    _sidebarBorder.Visibility = Visibility.Collapsed;
                    EndSidebarSlide();
                });
                // Splitter stays enabled so the user can grab it and drag the sidebar back open.
            }
            else
            {
                _sidebarBorder.Visibility = Visibility.Visible;
                double restore = _sidebarShowingOutlines ? _savedOutlinesWidth : _savedPagesWidth;
                // Slide in at full size: content fixed at the target width from the first
                // frame, revealed by the growing border instead of reflowing up to size.
                BeginSidebarSlide(_sbSlideContentW > 0 ? _sbSlideContentW
                    : Math.Max(0, restore / Math.Max(0.01, _appScale) - 24));
                AnimateSidebarWidth(restore, () =>
                {
                    _sidebarCol.MinWidth = SbPx(SidebarMinOpen);   // open: clamp so the list can't be dragged below readable
                    EndSidebarSlide();
                });
                _sidebarToggleBtn.ToolTip = Loc("Str_TT_CollapseSidebar");
                SidebarSplitter.IsEnabled = true;
            }
            UpdateSidebarToggleGlyph();
        }

        // Sidebar slide freeze: while the column animates, the content keeps a FIXED width
        // (anchored at the outer edge) and the border clips it - so thumbnails and labels
        // hold their size and slide out of view instead of reflowing every frame. Restored
        // to normal stretch layout when the glide lands.
        private double _sbSlideContentW;

        private void BeginSidebarSlide(double contentW)
        {
            if (contentW <= 0) return;
            _sbSlideContentW = contentW;
            SidebarContentPanel.Width = contentW;
            SidebarContentPanel.HorizontalAlignment =
                _sidebarRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            _sidebarBorder.ClipToBounds = true;
        }

        private void EndSidebarSlide()
        {
            SidebarContentPanel.Width = double.NaN;
            SidebarContentPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            _sidebarBorder.ClipToBounds = false;
        }

        // Animates the sidebar column between widths (toggle collapse / expand). No explicit
        // re-render here: every frame fires PagePreviewPanel_SizeChanged, whose lite fit keeps
        // the page tracking the pane smoothly and whose settle timer runs ONE crisp pass a beat
        // after the last frame - exactly the splitter-drag pipeline. An extra RefreshPageView at
        // Completed doubled up with that settle pass and read as a two-step stutter. Completion
        // clears the animation and writes the target as a plain local value so the splitter,
        // side flip, and full screen can keep setting Width directly.
        private void AnimateSidebarWidth(double target, Action? onDone = null)
        {
            var anim = new GridLengthAnimation
            {
                From     = new GridLength(Math.Max(0, _sidebarCol.ActualWidth)),
                To       = new GridLength(target),
                Duration = TimeSpan.FromMilliseconds(280),
                Easing   = new System.Windows.Media.Animation.CubicEase
                           { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
            };
            anim.Completed += (_, _) =>
            {
                _sidebarCol.BeginAnimation(ColumnDefinition.WidthProperty, null);
                _sidebarCol.Width = new GridLength(target);
                onDone?.Invoke();
            };
            _sidebarCol.BeginAnimation(ColumnDefinition.WidthProperty, anim);
        }

        // Pressing the splitter while the sidebar is collapsed begins pulling it open: reveal the page
        // list (still 0-width against the 24px strip, so there's no flash) so the drag grows it live. If
        // the user doesn't drag past the threshold, OnSidebarResized snaps it shut again on release.
        private bool _sidebarDragOpening;   // true while dragging the splitter open from the collapsed strip
        private bool _sidebarWantClose;     // set during an open-resize drag when pulled past the close edge

        private void OnSidebarSplitterPress()
        {
            if (!_sidebarCollapsed) return;
            // Begin pulling open from the strip. Show the sidebar background + grain right away so the
            // growing strip matches the sidebar, but keep the content (header + list) hidden until it's
            // pulled to the readable minimum (OnSidebarSplitterMove) - so nothing shows clipped, and the
            // column grows from the 24px edge tracking the mouse with no dead zone.
            _sidebarCollapsed = false;
            _sidebarDragOpening = true;
            _sidebarBorder.Visibility = Visibility.Visible;
            SidebarContentPanel.Visibility = Visibility.Collapsed;
            _sidebarToggleBtn.ToolTip = Loc("Str_TT_CollapseSidebar");
            UpdateSidebarToggleGlyph();
        }

        // Drives the splitter drag. While opening from the strip, reveal the list only once it's past the
        // readable minimum. Once open, pulling the mouse well past the minimum closes the sidebar (the
        // MinWidth clamp already stops the column from ever shrinking below the readable floor).
        private void OnSidebarSplitterMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_sidebarCollapsed || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
            if (_sidebarDragOpening)
            {
                SidebarContentPanel.Visibility = _sidebarCol.ActualWidth >= SbPx(SidebarMinOpen)
                    ? Visibility.Visible : Visibility.Collapsed;
                return;
            }
            // The column is clamped at the readable minimum (MinWidth), so it can't clip and the document
            // isn't resized as you pull. Just record intent; the actual close (and its single re-render)
            // happens on release in OnSidebarResized - so dragging the splitter never re-renders the page.
            double mx = e.GetPosition(MainContentGrid).X;
            const double closeBuffer = 36;   // pull this far past the minimum edge to close on release
            _sidebarWantClose = _sidebarRight
                ? mx > MainContentGrid.ActualWidth - SbPx(SidebarMinOpen) + closeBuffer
                : mx < SbPx(SidebarMinOpen) - closeBuffer;
        }

        // Called when the user finishes dragging the sidebar splitter. If they dragged it narrower than
        // the close threshold, snap it fully closed (to the toggle strip) instead of leaving an unusable
        // sliver; otherwise remember the new width for the current mode.
        private void OnSidebarResized()
        {
            _sidebarDragOpening = false;
            if (_sidebarCollapsed) { _sidebarWantClose = false; return; }
            if (_sidebarWantClose) { _sidebarWantClose = false; CollapseSidebarToStrip(); return; }
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
            {
                if (_sidebarCollapsed) return;
                double w = _sidebarCol.ActualWidth;
                if (w < SbPx(SidebarMinOpen))
                {
                    // Released below the readable minimum: snap to whichever end the user let go nearer
                    // to - fully closed, or the minimum open width.
                    double mid = SbPx((24 + SidebarMinOpen) / 2.0);
                    if (w < mid) { CollapseSidebarToStrip(); return; }
                    _sidebarCol.Width = new GridLength(SbPx(SidebarMinOpen));
                    w = SbPx(SidebarMinOpen);
                }
                _sidebarBorder.Visibility = Visibility.Visible;        // ensure the list shows when settled open
                SidebarContentPanel.Visibility = Visibility.Visible;
                _sidebarCol.MinWidth = SbPx(SidebarMinOpen);           // clamp future resizes so they can't clip
                if (_sidebarShowingOutlines) _savedOutlinesWidth = Math.Min(w, SbPx(SidebarMaxOutlines));
                else _savedPagesWidth = Math.Min(w, SbPx(SidebarMaxPages));
            }));
        }

        // Collapse to the 24px toggle strip without overwriting the saved width, so re-expand restores the
        // last good size rather than the thin dragged one. Mirrors SidebarToggle_Click's collapse branch.
        private void CollapseSidebarToStrip()
        {
            _sidebarCollapsed = true;
            _sidebarToggleBtn.ToolTip = Loc("Str_TT_ExpandSidebar");
            _sidebarBorder.Visibility = Visibility.Collapsed;
            SidebarContentPanel.Visibility = Visibility.Visible;   // reset so the next border-show has content
            _sidebarCol.Width = new GridLength(SbPx(24));
            _sidebarCol.MinWidth = SbPx(24);
            UpdateSidebarToggleGlyph();   // splitter stays enabled so it can be dragged back open
        }

        // The rail auto-collapses when no PDF is open and re-opens when one is (2026-07-23): with
        // nothing loaded there are no thumbnails to show, so the strip gets out of the way. The empty
        // page-jump box + "/ -" hide alongside. Only fires on the open/close transition (FinishOpenFile /
        // ShowEmptyState) and at startup - NOT on tab switches - so a manual toggle mid-session sticks.
        // startup=true collapses instantly (no glide before the first paint); runtime transitions animate.
        //
        // A sidebar the USER closed stays closed: the auto-open may only undo this method's OWN
        // collapse. Materializing a lazy tab runs the document-open transition too, so without the
        // distinction a tab click was popping a deliberately closed sidebar back open.
        private bool _sidebarAutoCollapsed;   // the current collapse was made HERE, not by the user
        private bool _sidebarAutoToggling;    // this method is driving SidebarToggle_Click

        private void SyncSidebarToDocState(bool hasDoc, bool startup)
        {
            PageControlsRow?.Visibility = (hasDoc && !_sidebarShowingOutlines)
                    ? Visibility.Visible : Visibility.Collapsed;

            _sidebarAutoToggling = true;
            try
            {
                if (hasDoc && _sidebarCollapsed && _sidebarAutoCollapsed)
                {
                    _sidebarAutoCollapsed = false;
                    SidebarToggle_Click(this, new RoutedEventArgs());          // open the rail for the document
                }
                else if (!hasDoc && !_sidebarCollapsed)
                {
                    _sidebarAutoCollapsed = true;
                    if (startup) CollapseSidebarToStrip();                     // instant at launch
                    else SidebarToggle_Click(this, new RoutedEventArgs());     // animated on last-tab close
                }
            }
            finally { _sidebarAutoToggling = false; }
        }
    }
}
