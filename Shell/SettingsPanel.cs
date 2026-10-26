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
        // Picker state sync + appearance handlers. (The Settings panel itself retired 2026-07-31:
        // every section moved to where the thing it configures lives - theme/language/view onto
        // rail flyouts, toolbar onto the bar's right-click menu, sidebar side onto the sidebar's.)
        // ============================================================

        // Syncs every picker's radios and accent dots to live state before a flyout shows -
        // exactly ONE sync implementation, shared by all the rail flyouts (Shell/RailFlyouts.cs).
        private void SyncPickerState()
        {
            var cur = ThemeManager.Current;
            ThemeDarkRadio.IsChecked  = cur == Theme.Dark;
            ThemeLightRadio.IsChecked = cur == Theme.Light;
            ThemeHCRadio.IsChecked    = cur == Theme.Black;
            Theme98SERadio.IsChecked  = cur == Theme.SE98;
            ThemeBloodRadio.IsChecked = cur == Theme.Blood;
            ThemeGreedRadio.IsChecked    = cur == Theme.Greed;
            ThemeCyanoticRadio.IsChecked = cur == Theme.Cyanotic;
            ThemeEctoplasmRadio.IsChecked = cur == Theme.Ectoplasm;
            ThemeDecayRadio.IsChecked = cur == Theme.Decay;
            ThemeMourningRadio.IsChecked = cur == Theme.Mourning;
            ThemeSepulchreRadio.IsChecked = cur == Theme.Sepulchre;
            ThemeDeliriumRadio.IsChecked = cur == Theme.Delirium;
            ThemeMalaiseRadio.IsChecked = cur == Theme.Malaise;
            UpdateAccentStrip(animate: false);
            // Sync language picker
            var curLoc = PewDF.Services.LocaleManager.Current;
            LangEnRadio.IsChecked   = curLoc == PewDF.Services.Locale.EnUS;
            LangCsRadio.IsChecked   = curLoc == PewDF.Services.Locale.CsCZ;
            LangEsRadio.IsChecked   = curLoc == PewDF.Services.Locale.Es;
            LangFrRadio.IsChecked   = curLoc == PewDF.Services.Locale.Fr;
            LangZhTWRadio.IsChecked = curLoc == PewDF.Services.Locale.ZhTW;
            LangZhCNRadio.IsChecked = curLoc == PewDF.Services.Locale.ZhCN;
            LangBnRadio.IsChecked   = curLoc == PewDF.Services.Locale.Bn;
            LangTrRadio.IsChecked   = curLoc == PewDF.Services.Locale.TrTR;
            LangNbRadio.IsChecked   = curLoc == PewDF.Services.Locale.NbNO;
            LangPtRadio.IsChecked   = curLoc == PewDF.Services.Locale.PtBR;
            LangUkRadio.IsChecked   = curLoc == PewDF.Services.Locale.UkUA;
            LangViRadio.IsChecked   = curLoc == PewDF.Services.Locale.ViVN;
            LangDeRadio.IsChecked   = curLoc == PewDF.Services.Locale.De;
            LangJaRadio.IsChecked   = curLoc == PewDF.Services.Locale.JaJP;
            LangPlRadio.IsChecked   = curLoc == PewDF.Services.Locale.PlPL;
            LangHuRadio.IsChecked   = curLoc == PewDF.Services.Locale.HuHU;
            // Visual Studio can retain an older design-time XAML field name in MainWindow.g.i.cs
            // after this picker changes. Resolve the radio through the stable submenu namescope so
            // both a fresh build and a stale design-time pass remain valid.
            var italianRadio = LangSubmenu.FindName("LangItalianRadio") as RadioButton
                ?? LangSubmenu.FindName("LangItRadio") as RadioButton;
            italianRadio?.IsChecked = curLoc == PewDF.Services.Locale.ItIT;
            LangKkRadio.IsChecked   = curLoc == PewDF.Services.Locale.KkKZ;
            LangRuRadio.IsChecked   = curLoc == PewDF.Services.Locale.RuRU;
            // Sync view mode radios. Against the PENDING mode while a fade-wrapped switch is in
            // flight (_viewMode lags until the fade-out lands), so wheel-cycling with the flyout
            // open moves the checkmark in step instead of one notch behind.
            var vm = _pendingViewMode ?? _viewMode;
            ViewSingleRadio.IsChecked     = vm == ViewMode.Single;
            ViewContinuousRadio.IsChecked = vm == ViewMode.Continuous;
            ViewTwoPageRadio.IsChecked    = vm == ViewMode.TwoPage;
            ViewGridRadio.IsChecked       = vm == ViewMode.Grid;
            // Active ribbon labels use shared DynamicResource values (UI_LOCALIZATION_CONTRACT).
        }

        // ── Quick fade in/out for the full-window overlay panels (Shortcuts/About) ──
        private static void FadeOverlayIn(UIElement el)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 0;
            el.Visibility = Visibility.Visible;
            el.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(110)))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }

        private static void FadeOverlayOut(UIElement el)
        {
            if (el.Visibility != Visibility.Visible) return;
            var anim = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(90)))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            anim.Completed += (_, _) =>
            {
                el.Visibility = Visibility.Collapsed;
                el.BeginAnimation(UIElement.OpacityProperty, null);
                el.Opacity = 1;
            };
            el.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        // Fades an annotate (draw/text) settings bar out over ~90ms, then removes it from its parent -
        // so the bar dissolves when its tool is deselected and crossfades when switching tools, matching
        // the About/Settings overlays.
        private static void FadeOutAndRemoveBar(Border? bar)
        {
            if (bar is null) return;
            var anim = new DoubleAnimation(bar.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(90)))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            anim.Completed += (_, _) =>
            {
                bar.BeginAnimation(UIElement.OpacityProperty, null);
                // Release the 98SE scroller inset before the bar leaves its area, so the document
                // slides back up to the pane top when the band disappears.
                if (bar.Parent is Grid area) SyncSe98BarInset(bar, area, removing: true);
                (bar.Parent as Panel)?.Children.Remove(bar);
            };
            bar.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        // Collapses the visible annotate bar to a thin peek strip, or expands it back. Triggered by
        // re-clicking the already-active tool, so a second click tucks the bar away instead of the
        // old behavior of rebuilding it (which flickered).
        private void ToggleAnnotBarMinimized()
        {
            var bar = _textSettingsBar ?? _drawSettingsBar ?? _cropConfirmBar;
            if (bar is null) return;
            _annotBarMinimized = !_annotBarMinimized;
            bar.ClipToBounds = true;
            const double peek = 13;   // thin strip, just enough for the grip dots
            if (_annotBarMinimized)
            {
                // Freeze the current width so collapsing the content can't shrink the bar to the dots and
                // slide it to the corner - it stays a same-width strip in place.
                bar.Width = bar.ActualWidth;
                bar.Effect = null;   // minimized strips never carry a drop shadow
                _annotBarFullHeight = bar.ActualHeight > 0 ? bar.ActualHeight : bar.DesiredSize.Height;
                var anim = new DoubleAnimation(_annotBarFullHeight, peek, new Duration(TimeSpan.FromMilliseconds(120)))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
                anim.Completed += (_, _) =>
                {
                    _annotBarContent?.Visibility = Visibility.Collapsed;
                    _annotBarDots?.Visibility = Visibility.Visible;
                    bar.ClipToBounds = false;   // content is hidden now, nothing to clip
                };
                bar.BeginAnimation(FrameworkElement.HeightProperty, anim);
            }
            else
            {
                // Show the full content again before growing back, and let the width track content again.
                bar.Width = double.NaN;
                bar.Effect = AnnotBarShadow();   // restore the drop shadow on the expanded bar
                _annotBarContent?.Visibility = Visibility.Visible;
                _annotBarDots?.Visibility = Visibility.Collapsed;
                double full = _annotBarFullHeight > 0 ? _annotBarFullHeight : bar.ActualHeight;
                var anim = new DoubleAnimation(peek, full, new Duration(TimeSpan.FromMilliseconds(120)))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                anim.Completed += (_, _) =>
                {
                    bar.BeginAnimation(FrameworkElement.HeightProperty, null);
                    bar.Height = double.NaN;   // back to auto so it tracks its content again
                    bar.ClipToBounds = false;
                };
                bar.BeginAnimation(FrameworkElement.HeightProperty, anim);
            }
        }

        // Confirm-before-opening-links (About card footer). One key, positive sense - see the
        // ConfirmLinksSetting comment in Links.cs. Writing the same value back on the init sync is
        // a harmless no-op, so this needs no change guard.
        private void LinkConfirmCheck_Toggled(object sender, RoutedEventArgs e)
            => App.SetSetting(ConfirmLinksSetting, LinkConfirmCheck.IsChecked == true ? "1" : "0");

        // Privacy section (#146): don't remember recently opened files. Turning it ON also clears
        // the existing list (matching the user's privacy expectation on a shared machine); the
        // guard makes the settings-open sync a no-op so opening the panel never wipes anything.
        private void NoRecentCheck_Toggled(object sender, RoutedEventArgs e)
        {
            bool off = NoRecentCheck.IsChecked == true;
            if ((App.GetSetting(App.NoRecentFilesSetting) == "1") == off) return;   // sync, not a user change
            if (off)
            {
                App.SetSetting(App.NoRecentFilesSetting, "1");
                App.ClearRecentFiles();
                PopulateRecentFilesList();   // start screen hides its Recent box immediately
            }
            else
            {
                App.RemoveSetting(App.NoRecentFilesSetting);
            }
        }

        // SEMANTIC ANCHOR: INVERT_MODE_REMOVED. No user-facing document color inversion;
        // rendering always uses the PDF's original colors. Do not reintroduce a rail toggle.

        // SEMANTIC ANCHOR: WINDOW_FADE_REFRESH_INVARIANT.
        // Coalesce size-triggered tab-strip/footer fade updates to one render-priority pass.
        // This belongs to window layout, not to the removed legacy toolbar.
        // SSOT: UpdateTabStripFade() owns the actual fade calculation.
        private bool _fadeRefreshQueued;
        private void ScheduleFadeRefresh()
        {
            if (_fadeRefreshQueued) return;
            _fadeRefreshQueued = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, (Action)(() =>
            {
                _fadeRefreshQueued = false;
                UpdateTabStripFade();
            }));
        }

        private void OnThemeChanged()
        {
            _appliedSquared = null;
            UpdateWindowChrome();
            // The 98SE pane reaches the frame while rounded themes retain an 8px outer gutter.
            // Reapply the saved sidebar side so switching away from 98SE restores that right edge.
            ApplySidebarSide();
            // Refresh snapshot FindResource calls that were set as local values.
            // SetResourceReference bindings update automatically; sidebar tabs and
            // active tool button background still need an explicit refresh.
            // SEMANTIC ANCHOR: TOOL_STATE_RESTORE_NO_TOGGLE
            // Refreshes of theme resources may redraw an active tool, but must not toggle its pane.
            SetTool(_currentTool, restoringPane: true);
            if (_sidebarShowingOutlines)
                SwitchSidebarToOutlinesTab();
            else
                SwitchSidebarToPagesTab();
            // Re-evaluate the page-list edge overlays immediately. 98SE sets the theme
            // multiplier to zero; modern themes leave it at the default full strength.
            SyncPageListEdgeFades();
            RefreshSelectionAccent();
            // Both panes carry local border-thickness state. Rebuild both so an inactive pane cannot
            // retain the previous theme's right-edge geometry until it happens to receive focus.
            Viewer.RebuildTabStripExt();
            ViewerB.RebuildTabStripExt();
            // The signature popup is built from snapshot (FindResource) colors, so rebuild it in place
            // if it's open so it picks up the new theme without the user having to close and reopen it.
            if (_signaturePopup is not null) ShowSignaturePopup();

            // The keyboard's layer selectors use the current theme's actual button template
            // (98SE raised bevel versus the modern card button), so rebuild the lazy board when
            // that template family changes. Key labels and mappings remain table-driven.
            if (_kbBuilt)
            {
                var layer = _kbLayer;
                _kbBuilt = false;
                BuildKeyboardView();
                SetKbLayer(layer);
            }

            // Code-built annotate bars can be open in either pane. Refresh both panes so a bar
            // cannot retain the accent or control template from the theme that created it.
            RefreshOpenAnnotationBars();
        }

        private void ThemeDarkRadio_Checked(object sender, RoutedEventArgs e)     => SelectTheme(Theme.Dark);
        private void ThemeLightRadio_Checked(object sender, RoutedEventArgs e)    => SelectTheme(Theme.Light);
        private void ThemeHCRadio_Checked(object sender, RoutedEventArgs e)       => SelectTheme(Theme.Black);
        private void Theme98SERadio_Checked(object sender, RoutedEventArgs e)     => SelectTheme(Theme.SE98);
        private void ThemeBloodRadio_Checked(object sender, RoutedEventArgs e)    => SelectTheme(Theme.Blood);
        private void ThemeGreedRadio_Checked(object sender, RoutedEventArgs e)    => SelectTheme(Theme.Greed);
        private void ThemeCyanoticRadio_Checked(object sender, RoutedEventArgs e) => SelectTheme(Theme.Cyanotic);
        private void ThemeEctoplasmRadio_Checked(object sender, RoutedEventArgs e) => SelectTheme(Theme.Ectoplasm);
        private void ThemeDecayRadio_Checked(object sender, RoutedEventArgs e)     => SelectTheme(Theme.Decay);
        private void ThemeMourningRadio_Checked(object sender, RoutedEventArgs e)  => SelectTheme(Theme.Mourning);
        private void ThemeSepulchreRadio_Checked(object sender, RoutedEventArgs e) => SelectTheme(Theme.Sepulchre);
        private void ThemeDeliriumRadio_Checked(object sender, RoutedEventArgs e)  => SelectTheme(Theme.Delirium);
        private void ThemeMalaiseRadio_Checked(object sender, RoutedEventArgs e)   => SelectTheme(Theme.Malaise);

        private void SelectTheme(Theme theme)
        {
            bool wasOpen = ThemeFlyout is not null && ThemeFlyout.IsOpen;
            ThemeManager.Apply(theme);
            UpdateAccentStrip(animate: true);
            // Intentionally leave the flyout open so the user can try another theme right away
            // without reopening the submenu. The theme swap's side effects (tab strip rebuild,
            // tool re-select) can still knock the popup closed behind our back, so check once
            // layout has settled and quietly reopen it in place if that happened.
            if (wasOpen)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    (Action)(() => { if (ThemeFlyout is not null && !ThemeFlyout.IsOpen) ThemeFlyout.IsOpen = true; }));
        }

        // #199 (Ryokoxx's design): ONE vertical strip of six swatches beside the theme list,
        // repainted per family, instead of a row under each accented theme. The list height is
        // identical for every theme, so the bottom-pinned card never moves; only the strip's
        // width animates, growing rightward from the pinned left edge. Per-family accent memory
        // in ThemeManager is untouched.
        private void AccentStripDot_Click(object sender, MouseButtonEventArgs e) => HandleAccentDot(sender, _stripFamily);

        private void HandleAccentDot(object sender, Theme family)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;
            if (!Enum.TryParse<DarkAccent>(tag, out var accent)) return;
            ThemeManager.ApplyAccent(family, accent);   // persists for that family; reapplies if active
            RingAccentStrip();
        }

        // Per-family swatch colors, in each family's display order (moved here from the four
        // retired XAML accent rows; the strip repaints these onto its eight shared dots).
        private static readonly (DarkAccent Accent, string Hex)[] DarkStripColors =
            [(DarkAccent.Red, "#DD504B"), (DarkAccent.Orange, "#E8962C"), (DarkAccent.Yellow, "#EAD900"), (DarkAccent.Green, "#1EA54C"),
             (DarkAccent.Teal, "#1FB8A8"), (DarkAccent.Blue, "#4580D9"), (DarkAccent.Purple, "#B982E3"), (DarkAccent.Magenta, "#FF52C9")];
        private static readonly (DarkAccent Accent, string Hex)[] LightStripColors =
            [(DarkAccent.Red, "#931A1A"), (DarkAccent.Orange, "#C7710F"), (DarkAccent.Yellow, "#EAD900"), (DarkAccent.Green, "#1B5E20"),
             (DarkAccent.Teal, "#0D827E"), (DarkAccent.Blue, "#18608E"), (DarkAccent.Purple, "#5A1690"), (DarkAccent.Magenta, "#A60070")];
        private static readonly (DarkAccent Accent, string Hex)[] BlackStripColors =
            [(DarkAccent.Red, "#FF2929"), (DarkAccent.Orange, "#FF910A"), (DarkAccent.Yellow, "#FFEB00"), (DarkAccent.Green, "#00FF66"),
             (DarkAccent.Teal, "#0AFFE7"), (DarkAccent.Blue, "#298DFF"), (DarkAccent.Purple, "#B829FF"), (DarkAccent.Magenta, "#FF2BBD")];
        private static readonly (DarkAccent Accent, string Hex)[] SE98StripColors =
            [(DarkAccent.Red, "#800000"), (DarkAccent.Orange, "#A05000"), (DarkAccent.Yellow, "#EAD900"), (DarkAccent.Green, "#006000"),
             (DarkAccent.Teal, "#008080"), (DarkAccent.Blue, "#000080"), (DarkAccent.Purple, "#5A376E"), (DarkAccent.Magenta, "#750052")];

        private static (DarkAccent Accent, string Hex)[] StripColorsFor(Theme family) => family switch
        {
            Theme.Light => LightStripColors,
            Theme.Black => BlackStripColors,
            Theme.SE98  => SE98StripColors,
            _           => DarkStripColors,
        };

        private Theme _stripFamily = Theme.Dark;   // the family the strip's dots currently show
        private bool  _stripOpen;
        private const double AccentStripWidth   = 39;    // 1px rule + 10 gap + 26 swatch + 2 air
        private const double AccentStripSlideMs = 180;

        private Border[] StripDots =>
            [AccentStripDot0, AccentStripDot1, AccentStripDot2, AccentStripDot3, AccentStripDot4, AccentStripDot5, AccentStripDot6, AccentStripDot7];

        // Each pill shows its accent the way a selection does: the palette's own SelectionBg
        // gradient, read from that accent's theme file. 98SE keeps its flat Win98 swatches.
        private static readonly Dictionary<(Theme, DarkAccent), System.Windows.Media.Brush> AccentStripBrushes = [];

        private static System.Windows.Media.Brush AccentStripBrush(Theme family, DarkAccent accent, string flatHex)
        {
            if (AccentStripBrushes.TryGetValue((family, accent), out var cached)) return cached;
            System.Windows.Media.Brush brush;
            if (family == Theme.SE98)
                brush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(flatHex));
            else
            {
                string path = accent == DarkAccent.Green
                    ? $"Themes/{family}.xaml"
                    : $"Themes/Accents/{family}/{accent}.xaml";
                var palette = new ResourceDictionary
                {
                    // Release payloads use PewDF.App; normal builds use PewDF.
                    Source = new Uri($"/{typeof(MainWindow).Assembly.GetName().Name};component/{path}", UriKind.Relative)
                };
                brush = ((System.Windows.Media.Brush)palette["SelectionBg"]).CloneCurrentValue();
            }
            brush.Freeze();
            AccentStripBrushes[(family, accent)] = brush;
            return brush;
        }

        private bool _stripHoverWired;

        // The pills lift a little under the mouse (KillerNotes). The dots are declared in
        // MainWindow.xaml, so the transform and the hover hooks are attached once, here.
        private void WireAccentStripHover()
        {
            if (_stripHoverWired) return;
            _stripHoverWired = true;
            foreach (var dot in StripDots)
            {
                dot.RenderTransformOrigin = new Point(0.5, 0.5);
                dot.RenderTransform = new ScaleTransform();
                dot.MouseEnter += (_, _) => RingAccentStrip();
                dot.MouseLeave += (_, _) => RingAccentStrip();
            }
        }

        private void PopulateAccentStrip(Theme family)
        {
            WireAccentStripHover();
            var colors = StripColorsFor(family);
            var dots = StripDots;
            for (int i = 0; i < dots.Length; i++)
            {
                dots[i].Background = AccentStripBrush(family, colors[i].Accent, colors[i].Hex);
                dots[i].Tag = colors[i].Accent.ToString();
            }
            _stripFamily = family;
            RingAccentStrip();
        }

        // Ring the strip's selected swatch for the family it is showing.
        private void RingAccentStrip()
        {
            if (AccentStrip is null) return;
            var ring = (System.Windows.Media.Brush)FindResource("TextBrush");
            var chosen = _stripFamily switch
            {
                Theme.Light => ThemeManager.LightAccentChoice,
                Theme.Black => ThemeManager.BlackAccentChoice,
                Theme.SE98  => ThemeManager.SE98AccentChoice,
                _           => ThemeManager.DarkAccentChoice,
            };
            foreach (var dot in StripDots)
            {
                bool sel = dot.Tag is string t && Enum.TryParse<DarkAccent>(t, out var a) && a == chosen;
                dot.BorderBrush = dot.IsMouseOver || sel ? ring : System.Windows.Media.Brushes.Transparent;
                bool pop = dot.IsMouseOver && _stripFamily != Theme.SE98;
                if (dot.RenderTransform is ScaleTransform scale)
                {
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                        new DoubleAnimation(pop ? 1.06 : 1, TimeSpan.FromMilliseconds(100)));
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                        new DoubleAnimation(pop ? 1.03 : 1, TimeSpan.FromMilliseconds(100)));
                }
                dot.Effect = pop
                    ? new System.Windows.Media.Effects.DropShadowEffect
                    { Color = System.Windows.Media.Colors.Black, BlurRadius = 4, ShadowDepth = 1, Opacity = 0.25 }
                    : null;
            }
        }

        private void UpdateAccentStrip(bool animate)
        {
            var cur = ThemeManager.Current;
            bool show = cur is Theme.Dark or Theme.Light or Theme.Black or Theme.SE98;
            if (show)
            {
                if (animate && _stripOpen && _stripFamily != cur)
                {
                    // Accented -> accented: the strip stays open and the swatches CROSSFADE.
                    // Re-running the open animation here snapped the strip shut and back.
                    var target = cur;
                    var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(90));
                    fadeOut.Completed += (_, __) =>
                    {
                        PopulateAccentStrip(target);
                        AccentStrip.BeginAnimation(OpacityProperty,
                            new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(90)));
                    };
                    AccentStrip.BeginAnimation(OpacityProperty, fadeOut);
                }
                else PopulateAccentStrip(cur);
            }
            SlideAccentStrip(show, animate);
        }

        // Width slide, eased - one element animates now, so the old linear lockstep constraint
        // that kept two rows summing to a constant height no longer applies.
        private void SlideAccentStrip(bool show, bool animate)
        {
            if (show == _stripOpen && animate) return;   // already there; crossfade handles content
            _stripOpen = show;
            AccentStripHost.BeginAnimation(WidthProperty, null);   // drop any in-flight slide
            if (!animate) { AccentStripHost.Width = show ? AccentStripWidth : 0; return; }
            double from = double.IsNaN(AccentStripHost.Width) ? AccentStripHost.ActualWidth : AccentStripHost.Width;
            var anim = new System.Windows.Media.Animation.DoubleAnimation(from, show ? AccentStripWidth : 0,
                TimeSpan.FromMilliseconds(AccentStripSlideMs))
            {
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            anim.Completed += (_, __) =>
            {
                AccentStripHost.BeginAnimation(WidthProperty, null);
                AccentStripHost.Width = _stripOpen ? AccentStripWidth : 0;
            };
            AccentStripHost.BeginAnimation(WidthProperty, anim);
        }

        // Localized display name for each theme, shown on the picker row.

        // #211: translator test mode - when the external translation file live-reloads, re-run the
        // same rebuilds a language switch does, so code-built captions update alongside the
        // DynamicResource strings. Called once from the window ctor; the event only ever fires
        // when the app was started with --lang-file.
        internal void HookExternalLangReload()
            => PewDF.Services.LocaleManager.ExternalReloaded += () =>
            {

                BuildContextMenu();
                RefreshOpenAnnotationBars();
            };

        private void LangEnRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(PewDF.Services.Locale.EnUS);
        private void LangCsRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(PewDF.Services.Locale.CsCZ);
        private void LangEsRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(PewDF.Services.Locale.Es);
        private void LangFrRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(PewDF.Services.Locale.Fr);
        private void LangZhTWRadio_Checked(object sender, RoutedEventArgs e) => SelectLocale(PewDF.Services.Locale.ZhTW);
        private void LangZhCNRadio_Checked(object sender, RoutedEventArgs e) => SelectLocale(PewDF.Services.Locale.ZhCN);
        private void LangBnRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(PewDF.Services.Locale.Bn);
        private void LangTrRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(PewDF.Services.Locale.TrTR);
        private void LangNbRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(PewDF.Services.Locale.NbNO);
        private void LangPtRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(PewDF.Services.Locale.PtBR);
        private void LangUkRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(PewDF.Services.Locale.UkUA);
        private void LangViRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(PewDF.Services.Locale.ViVN);
        private void LangDeRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(PewDF.Services.Locale.De);
        private void LangJaRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(PewDF.Services.Locale.JaJP);
        private void LangPlRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(PewDF.Services.Locale.PlPL);
        private void LangHuRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(PewDF.Services.Locale.HuHU);
        private void LangItalianRadio_Checked(object sender, RoutedEventArgs e) => SelectLocale(PewDF.Services.Locale.ItIT);

        // Compatibility for Visual Studio's stale design-time MainWindow.g.i.cs. The real XAML
        // uses LangItalianRadio_Checked, but an older generated file may still bind this name.
        private void LangItRadio_Checked(object sender, RoutedEventArgs e) => SelectLocale(PewDF.Services.Locale.ItIT);
        private void LangKkRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(PewDF.Services.Locale.KkKZ);
        private void LangRuRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(PewDF.Services.Locale.RuRU);

        private void SelectLocale(PewDF.Services.Locale loc)
        {
            PewDF.Services.LocaleManager.Apply(loc);
            // SEMANTIC ANCHOR: UI_LOCALIZATION_CONTRACT: Rebuild code-generated overflow captions after resource dictionary swaps.
            if (_previewInitialized) ReflowRibbonPreview();

            LangFlyout.IsOpen = false;   // a pick closes the rail flyout, like the accordion used to collapse

            // The status bar text is a formatted string (not a DynamicResource), so it keeps the
            // language it was last set in. Re-set it in the new locale instead of leaving it stale.
            if (_doc is not null && PageList.SelectedIndex >= 0)
                SetStatus(string.Format(Loc("Str_PageOf"), PageList.SelectedIndex + 1, _doc.PageCount));
            else
                SetStatus(Loc("Str_Ready"));

            // The canvas right-click menu is built once with Loc() values captured at build time,
            // so rebuild it in the new language. (The sidebar menu is rebuilt on each open.)
            BuildContextMenu();


            // Refresh code-built annotate bars in both panes. A language change comes from a
            // window flyout, so the pane containing the visible bar is not guaranteed to be the
            // current ActiveViewer by the time these controls are rebuilt.
            RefreshOpenAnnotationBars();

            // Page thumbnails and outline tooltips snapshot Loc() strings when built; rebuild both
            // lists so their "Page N" labels switch to the new language immediately.
            RefreshPageList();
            RefreshOutlines();

            // A visible signature popup is built with Loc() too; rebuild it so its section headers and
            // pen labels switch immediately.
            RefreshSignaturePopupLanguage();

            if (_kbBuilt)
            {
                var layer = _kbLayer;
                _kbBuilt = false;
                BuildKeyboardView();
                SetKbLayer(layer);
            }

            // The fit-mode terms differ in length per language; resize the zoom box so the longest never clips.
            AdjustZoomBoxWidth();
        }

        private void RefreshOpenAnnotationBars()
        {
            var active = ActiveViewer;
            var other = ReferenceEquals(active, Viewer) ? ViewerB : Viewer;
            // Refresh the inactive pane first. BuildBarHost keeps a few drag/minimize references
            // at window scope, so finishing on the active pane leaves those references pointed at
            // the controls the shared toolbar is currently operating.
            if (!ReferenceEquals(other, active)) RefreshOpenAnnotationBars(other);
            RefreshOpenAnnotationBars(active);
        }

        private void RefreshOpenAnnotationBars(PewDF.Controls.PdfViewer viewer)
        {
            ((PewDF.Features.IViewerHost)this).RunWithViewerContext(viewer, () =>
            {
                var tool = _annotBarTool;
                if (tool == EditTool.Text)
                    ShowTextSettings();
                else if (tool is EditTool bt && bt is EditTool.Draw or EditTool.Highlight
                    or EditTool.Line or EditTool.Strikethrough or EditTool.Underline or EditTool.Shape)
                    ShowDrawSettings(bt);

                RebuildCropBarForLocale();
            });
        }

        // Keep the footer zoom box compact. Its popup has a wider minimum for localized fit labels.
        private void AdjustZoomBoxWidth()
        {
            if (ZoomBox is null) return;
            try
            {
                double pixelsPerDip = VisualTreeHelper.GetDpi(ZoomBox).PixelsPerDip;
                var typeface = new System.Windows.Media.Typeface(
                    ZoomBox.FontFamily, ZoomBox.FontStyle, ZoomBox.FontWeight, ZoomBox.FontStretch);
                double emSize = ZoomBox.FontSize > 0 ? ZoomBox.FontSize : 12;
                double max = 0;
                foreach (var item in ZoomBox.Items)
                {
                    string text = item is System.Windows.Controls.ComboBoxItem ci
                        ? ci.Content?.ToString() ?? ""
                        : item?.ToString() ?? "";
                    var ft = new System.Windows.Media.FormattedText(
                        text, System.Globalization.CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
                        typeface, emSize, System.Windows.Media.Brushes.Black, pixelsPerDip);
                    if (ft.WidthIncludingTrailingWhitespace > max) max = ft.WidthIncludingTrailingWhitespace;
                }
                ZoomBox.Width = System.Math.Clamp(System.Math.Ceiling(max) + 28, 70, 220);
            }
            catch { /* best-effort; leave the XAML default width */ }
        }

        // Native name (autonym) for each language, shown in the picker regardless of UI locale.

        private void ViewContinuousRadio_Checked(object sender, RoutedEventArgs e) => SelectViewMode(ViewMode.Continuous);
        private void ViewSingleRadio_Checked(object sender, RoutedEventArgs e)     => SelectViewMode(ViewMode.Single);
        private void ViewTwoPageRadio_Checked(object sender, RoutedEventArgs e)    => SelectViewMode(ViewMode.TwoPage);
        private void ViewGridRadio_Checked(object sender, RoutedEventArgs e)       => SelectViewMode(ViewMode.Grid);

        // #193: book layout toggle (cover page alone in Two-Page). Global, persisted; both panes
        // re-run their current layout so an active Two-Page view re-pairs in place. Reached from
        // the page context menu while in Two-Page, and the bare B key.
        internal void ToggleBookMode()
        {
            App.SetSetting("TwoPageBook", Controls.PdfViewer.BookMode ? "0" : "1");
            Viewer.ReapplyViewMode();
            ViewerB.ReapplyViewMode();
        }

        // SEMANTIC ANCHOR: RIBBON_VISIBILITY_SINGLE_OWNER_INVARIANT.
        // The active ToolbarRowBorder is the only ribbon visibility target. The retired
        // toolbar icon-size/overflow engine must not control the current ribbon.
        private bool _toolbarHidden;
        internal void ToggleToolbarHidden()
        {
            _toolbarHidden = !_toolbarHidden;
            ToolbarRowBorder.Visibility = _toolbarHidden ? Visibility.Collapsed : Visibility.Visible;
            App.SetSetting("ToolbarHidden", _toolbarHidden ? "1" : "0");
            SetStatus(Loc(_toolbarHidden ? "Str_St_ToolbarHidden" : "Str_St_ToolbarShown"));
        }

    }
}
