// PewDF semantic anchor: one application-wide source for window caption title, icon and color.
// Native and custom captions share this identity; dialog-specific suffixes are retained.
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PewDF.Controls
{
    public static partial class WindowIdentity
    {
        public const string AppTitle = "PewDF";
        // SEMANTIC ANCHOR: APPLICATION_ICON_IDENTITY
        // INVARIANT/SSOT: This resource path must match the icon included in PewDF.csproj.
        public const string IconUri = "pack://application:,,,/Resources/pewdf-icon.ico";
        public static readonly Brush CaptionBackground = new SolidColorBrush(Color.FromRgb(32, 32, 32));
        public static readonly Brush CaptionForeground = Brushes.White;

        [LibraryImport("dwmapi.dll")]
        private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        internal static string NormalizeTitle(string? title)
        {
            string text = (title ?? "").Trim();
            if (text.Length == 0 || text.Equals(AppTitle, StringComparison.OrdinalIgnoreCase))
                return AppTitle;
            if (text.StartsWith(AppTitle + " - ", StringComparison.OrdinalIgnoreCase))
                return text;
            return AppTitle + " - " + text;
        }

        public static ImageSource Icon => new BitmapImage(new Uri(IconUri));

        internal static void Apply(Window window)
        {
            // Hide the identity from special-purpose transparent overlays, which have no caption.
            if (window.WindowStyle == WindowStyle.None && window.AllowsTransparency &&
                window.Background is SolidColorBrush b && b.Color.A <= 1 && window.Title.Length == 0)
                return;
            window.Title = NormalizeTitle(window.Title);
            window.Icon = Icon;
            if (window.WindowStyle != WindowStyle.SingleBorderWindow && window.WindowStyle != WindowStyle.ThreeDBorderWindow &&
                window.WindowStyle != WindowStyle.ToolWindow)
                return;
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            // Win11/modern DWM: dark gray in both active and inactive states where supported.
            int dark = 1;
            int color = 0x00202020;
            int foreground = 0x00FFFFFF;
            try
            {
                _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
                _ = DwmSetWindowAttribute(hwnd, 35, ref color, sizeof(int));
                _ = DwmSetWindowAttribute(hwnd, 36, ref foreground, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
    }
}
