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
using PewDF.Features;
using PewDF.Services;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace PewDF
{
    public partial class MainWindow : ISearchHost
    {
        // The search state machine (match list, cursor, next/prev) lives in
        // Features/Search/SearchController.cs; the bar UI, debounce, and highlight painting stay
        // here. Forwarders keep the bar buttons' and KeyboardShortcuts' call sites unchanged.
        private SearchController? _searchController;
        private SearchController Search => _searchController ??= new SearchController(this);

        private void SearchNextResult() => Search.Next();
        private void SearchPrevResult() => Search.Prev();

        // ---- ISearchHost -----------------------------------------------------------------------
        // (IShellServices - Window, Loc, SetStatus - is implemented once for the class in
        // Shell/About.cs.)

        string? ISearchHost.CurrentFile => _currentFile;
        int ISearchHost.CurrentPageIndex => PageList.SelectedIndex;
        void ISearchHost.GoToPage(int pageIdx) => PageList.SelectedIndex = pageIdx;

        void ISearchHost.SetResultText(string text)
        {
            _searchStatus?.Text = text;
        }

        void ISearchHost.SetResultCount(string text, string? tooltip)
        {
            if (_searchStatus is null) return;
            _searchStatus.Text = text;
            _searchStatus.ToolTip = tooltip;
        }

        void ISearchHost.ClearHighlights() => ClearSearchHighlights();
        void ISearchHost.RepaintHighlights() => HighlightSearchResultsOnCurrentPage();

        // SEMANTIC ANCHOR: Search SSOT is the inline toolbar TextBox and existing SearchController.
        // No floating duplicate field; no OS focus rectangle or duplicated search state.

        private void ToolbarSearchInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchBox = ToolbarSearchInput;
            _searchStatus = ToolbarSearchCount;
            SearchBox_TextChanged(sender, e);
        }

        private void ToolbarSearchInput_KeyDown(object sender, KeyEventArgs e)
        {
            _searchBox = ToolbarSearchInput;
            _searchStatus = ToolbarSearchCount;
            if (e.Key == Key.Escape)
            {
                ToolbarSearchInput.Clear();
                Keyboard.ClearFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                _searchDebounce?.Stop();
                if (!Search.HasResults && ToolbarSearchInput.Text.Length >= 2)
                    Search.Run(ToolbarSearchInput.Text);
                else if (Keyboard.Modifiers == ModifierKeys.Shift) SearchPrevResult();
                else SearchNextResult();
                e.Handled = true;
            }
        }

        private void ToolbarSearchPrevious_Click(object sender, RoutedEventArgs e) => SearchPrevResult();
        private void ToolbarSearchNext_Click(object sender, RoutedEventArgs e) => SearchNextResult();

        private void ToggleSearchBar()
        {
            _searchBox = ToolbarSearchInput;
            _searchStatus = ToolbarSearchCount;
            ToolbarSearchInput.Focus();
            Keyboard.Focus(ToolbarSearchInput);
            ToolbarSearchInput.SelectAll();
        }

        // SEMANTIC ANCHOR: INLINE_SEARCH_UI_SSOT.
        // The persistent ToolbarSearchInput and SearchController are the only search UI/state.
        // INVARIANT: Keyboard shortcuts focus the inline field; no duplicate floating bar.

        // VIEWER_SEARCH_HOST_COMPAT_INVARIANT: Viewer-host tab changes still clear highlights,
        // but the obsolete floating search UI must never be instantiated.
        private void CloseSearchBar() => ClearSearchHighlights();

        private System.Windows.Threading.DispatcherTimer? _searchDebounce;

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var text = _searchBox?.Text ?? "";
            if (text.Length < 2)
            {
                _searchDebounce?.Stop();
                ClearSearchHighlights();
                Search.ClearMatches();
                _searchStatus?.Text = "";
                return;
            }
            // Debounce: wait for a brief pause in typing before searching, so the first keystrokes
            // on a large document don't lock the UI while it searches partial queries.
            if (_searchDebounce is null)
            {
                _searchDebounce = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(250) };
                _searchDebounce.Tick += (_, _) =>
                {
                    _searchDebounce!.Stop();
                    var q = _searchBox?.Text ?? "";
                    if (q.Length >= 2) Search.Run(q);
                };
            }
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }

        // Paints match highlights onto EVERY page that's currently on screen and has results -
        // the primary tile and all per-page overlays alike. Single Page shows the one page; Two-Page
        // and Grid show every visible tile with hits; Continuous shows them down the whole scroll.
        // Re-paints one page's match highlights onto its overlay using the in-memory page size (no
        // file I/O), so it's cheap enough to call at the tail of every RenderAllAnnotations - which is
        // what keeps highlights alive instead of being wiped by re-renders and continuous scrolling.
        private void ApplySearchHighlights(int page, Canvas canvas)
        {
            if (_doc is null || page < 0 || page >= EnsureEngineDocumentSession().PageCount) return;
            if (!Search.TryGetPageRects(page, out var rects)) return;
            if (!_renderDims.TryGetValue(page, out var rd)) return;

            var (renderW, renderH) = rd;
            var pageInfo = EnsureEngineDocumentSession().Pages[page];
            double pdfW = pageInfo.Width;
            double pdfH = pageInfo.Height;
            if (pdfW <= 0 || pdfH <= 0) return;
            // Match rects are unrotated page points; the canvas shows the page turned by its rotation.
            int rotation = _pageRotations.TryGetValue(page, out int stored) ? stored : pageInfo.Rotation;

            var cur = Search.CurrentMatch;

            foreach (var (left, bottom, right, top) in rects)
            {
                bool isCurrent = cur is { } c && c.page == page
                    && c.left == left && c.bottom == bottom && c.right == right && c.top == top;
                AddSearchHighlight(canvas, PageCanvasGeometry.PageToCanvas(left, bottom, right, top,
                    pdfW, pdfH, renderW, renderH, rotation), isCurrent);
            }
        }

        // Repaints highlights on every page on screen right now (called when a search runs or the
        // current page changes); per-page re-renders keep them alive via ApplySearchHighlights.
        private void HighlightSearchResultsOnCurrentPage()
        {
            ClearSearchHighlights();
            foreach (var page in Search.PagesWithResults)
            {
                var canvas = VisibleCanvasForPage(page);
                if (canvas is not null) ApplySearchHighlights(page, canvas);
            }
        }

        private static void AddSearchHighlight(Canvas canvas, Rect match, bool isCurrent)
        {
            double cw = match.Width;
            double ch = match.Height;
            // A little breathing room so the box (and the current-match outline) wraps the whole word -
            // PdfPig's glyph bounds sit tight against the letters. Scales with text height so it looks
            // consistent across font sizes.
            // On a turned page the text height is the shorter side of the match.
            double pad = Math.Min(cw, ch) * 0.30;
            double cx = match.X - pad;
            double cy = match.Y - pad;
            var rect = new Rectangle
            {
                // The current match (search cursor) gets a brighter, more opaque fill; the others are dim.
                Fill = new SolidColorBrush(isCurrent
                    ? Color.FromArgb(150, 255, 190, 0)
                    : Color.FromArgb(70, 255, 165, 0)),
                StrokeThickness = isCurrent ? 3.5 : 1,
                RadiusX = pad * 0.6,
                RadiusY = pad * 0.6,
                Width = Math.Max(cw + pad * 2, 4),
                Height = Math.Max(ch + pad * 2, 4),
                IsHitTestVisible = false,
                Tag = "SearchHighlight"
            };
            if (isCurrent)
                // Bind the current-match outline to the on-page accent resource so it recolors live on a
                // theme switch (matching the selection chrome), rather than baking the color in at paint time.
                rect.SetResourceReference(Shape.StrokeProperty, "SelectionAccent");
            else
                rect.Stroke = new SolidColorBrush(Color.FromArgb(140, 255, 165, 0));
            Canvas.SetLeft(rect, cx);
            Canvas.SetTop(rect, cy);
            canvas.Children.Add(rect);
        }

        // Removes only the highlight rectangles from every page overlay. Deliberately does NOT touch
        // the result counter - that's owned by SearchController's status updates and the empty-query path - so a
        // repaint (which clears then re-adds highlights) can't wipe the "3 / 14" count.
        private void ClearSearchHighlights()
        {
            foreach (var canvas in AllPageCanvases())
            {
                var toRemove = canvas.Children.OfType<Rectangle>()
                    .Where(r => r.Tag is string s && s == "SearchHighlight").ToList();
                foreach (var r in toRemove)
                    canvas.Children.Remove(r);
            }
        }
    }
}
