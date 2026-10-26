using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PewDF
{
    /// <summary>
    /// Single-instance entry points: App forwards a second launch's file path here rather than
    /// starting another process.
    ///
    /// These stay on the window: RestoreAndActivate drives WindowState, Activate() and Topmost,
    /// which a UserControl does not have. The OpenInNewTab call routes through ActiveViewer so the
    /// forwarded file lands in whichever pane has focus.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>A forwarded path that arrived before the panes existed, replayed from Loaded.</summary>
        private string? _pendingExternalPath;

        public void OpenFromExternal(string? path)
        {
            // A second launch is forwarded here off the pipe thread, and Application.MainWindow is
            // already set while this window's constructor is still running - so ActiveViewer can
            // still be null (it is assigned by InitSplitPanes). Hold the path and let Loaded
            // replay it rather than dereferencing null and losing the file the user double-clicked.
            if (ActiveViewer == null)
            {
                _pendingExternalPath = path;
                return;
            }

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                ActiveViewer.OpenInNewTabExt(path!);
                return;
            }
            // SEMANTIC ANCHOR: EXTERNAL_OPEN_SSOT: External activation opens only existing
            // local files; remote URL downloading and custom protocol handoffs are unsupported.
        }

        /// <summary>Replay whatever arrived during startup. No-op in the normal case.</summary>
        internal void FlushPendingExternalOpen()
        {
            if (_pendingExternalPath == null) return;
            var path = _pendingExternalPath;
            _pendingExternalPath = null;
            OpenFromExternal(path);
        }

        public void RestoreAndActivate()
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero) SetForegroundWindow(handle);
            // Briefly toggle Topmost to pull the window in front without keeping it pinned.
            Topmost = true;
            Topmost = false;
            Focus();
        }

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetForegroundWindow(IntPtr hWnd);
    }
}
