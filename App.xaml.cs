using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using PewDF.Services;
using Microsoft.Win32;

namespace PewDF
{
    public partial class App : Application
    {
        // ============================================================
        // Paths
        // ============================================================

        private static readonly string AppName   = "PewDF";
        // PORTABLE_ONLY_STARTUP_INVARIANT: no installed-copy paths or installer state.

        // ============================================================
        // Shell interop
        // ============================================================

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AllowSetForegroundWindow(int dwProcessId);

        // ============================================================
        // Startup
        // ============================================================

        protected override void OnStartup(StartupEventArgs e)
        {
            KillerFamily.Accessibility.AccessibleControls.Initialize();
            if (IsStartupSmoke)
            {
                RunStartupSmoke(e);
                return;
            }
            DispatcherUnhandledException                    += OnDispatcherException;
            AppDomain.CurrentDomain.UnhandledException      += OnDomainException;
            TaskScheduler.UnobservedTaskException           += OnUnobservedTaskException;

            base.OnStartup(e);
            // All captioned windows inherit the same app identity, including dynamically created dialogs.
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) => { if (sender is Window w) PewDF.Controls.WindowIdentity.Apply(w); }));

            // The app-level grain tile also supports pre-window crash previews.
            EnsureCrashPreviewGrain();

            // SEMANTIC ANCHOR: GUI_ONLY_STARTUP.
            // INVARIANT: Startup never dispatches headless batch commands. File-association
            // arguments still flow through the existing single-instance GUI open path.
            // Developer-only visual check for the crash dialog. It runs before single-instance
            // forwarding so the preview can open beside a normal PewDF session.
            if (e.Args.Any(a => string.Equals(
                    a, "--crash-dialog-preview", StringComparison.OrdinalIgnoreCase)))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ThemeManager.Initialize();
                LocaleManager.Initialize();
                EnsureCrashPreviewGrain();
                string previewLog = Path.Combine(Path.GetTempPath(), "PewDF-crash-preview.log");
                var previewException = new NotSupportedException(
                    "Adding form widgets requires one top-level Document structure element.");
                try
                {
                    File.WriteAllText(previewLog, BuildFullCrashReport(previewException));
                    ShowCrashDialog(previewException, previewLog, isFatal: false);
                }
                finally
                {
                    try { File.Delete(previewLog); } catch { }
                }
                Shutdown(0);
                return;
            }

            // Single instance: a second launch (e.g. double-clicking another PDF in Explorer)
            // forwards its file path to the already-running instance, which opens it as a new
            // tab, then this process exits. Without this, every launch spawned its own window.
            bool isPrimary;
            try { _instanceMutex = new Mutex(true, MutexName, out isPrimary); }
            catch { isPrimary = true; }
            if (!isPrimary)
            {
                var fwd = e.Args.FirstOrDefault(a => !a.StartsWith('/'));
                GrantPrimaryForegroundPermission();
                ForwardToPrimary(fwd);
                Shutdown(0);
                return;
            }
            StartPipeServer();

            // SEMANTIC ANCHOR: EXTERNAL_OPEN_SSOT: Only local PDF paths are accepted from Explorer
            // and forwarded via the single-instance pipe; no browser protocol is registered.
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            CleanupStaleTemps();
            ThemeManager.Initialize();
            // #211: translator test mode - load an external translation file as the language
            // override and re-apply it on every save (see LocaleManager). Documented in
            // TRANSLATING.md; must be set before LocaleManager.Initialize applies the locale.
            for (int i = 0; i < e.Args.Length - 1; i++)
                if (string.Equals(e.Args[i], "--lang-file", StringComparison.OrdinalIgnoreCase))
                { try { LocaleManager.ExternalFile = Path.GetFullPath(e.Args[i + 1]); } catch { /* bad path: normal locale */ } }
            LocaleManager.Initialize();
            var mainWindow = new MainWindow();
            mainWindow.Show();
            // SEMANTIC ANCHOR: PDF_DEFAULT_PROMPT_PRIMARY_STARTUP
            // Never run this for second instances, smoke tests or pre-window preview modes.
            mainWindow.Dispatcher.BeginInvoke(new Action(mainWindow.OfferDefaultPdfOnFirstLaunch),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // ============================================================
        // Single-instance IPC (mutex + named pipe)
        // ============================================================

        private const string MutexName = @"Local\PewDF.SingleInstance";
        private const string PipeName  = "PewDF.OpenPipe";
        private Mutex? _instanceMutex;

        private void StartPipeServer()
        {
            var t = new System.Threading.Thread(RunPipeServer)
            {
                IsBackground = true,
                Name = "PewDF-IPC",
            };
            t.Start();
        }

        // Accepts forwarded file paths from secondary launches and hands them to the UI thread.
        private void RunPipeServer()
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string? path = reader.ReadLine();
                    Dispatcher.BeginInvoke(new Action(() => DeliverExternalOpen(path)));
                }
                catch
                {
                    System.Threading.Thread.Sleep(150);   // pipe error - back off, keep serving
                }
            }
        }

        private static void DeliverExternalOpen(string? path)
        {
            if (Current?.MainWindow is MainWindow mw)
            {
                mw.RestoreAndActivate();
                if (!string.IsNullOrEmpty(path)) mw.OpenFromExternal(path);
                mw.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                    new Action(mw.RestoreAndActivate));
            }
        }

        // Secondary instance: send our file path to the primary instance over the pipe.
        private static void ForwardToPrimary(string? path)
            => SendToPrimary(path);

        private static bool SendToPrimary(string? path)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(3000);
                using var w = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
                w.WriteLine(path ?? "");
                return true;
            }
            catch { return false; }
        }

        // Explorer launches the secondary process with foreground permission. Pass that permission
        // to the already-running PewDF process before asking it to activate its window.
        private static void GrantPrimaryForegroundPermission()
        {
            try
            {
                using Process current = Process.GetCurrentProcess();
                foreach (Process candidate in Process.GetProcessesByName(current.ProcessName)
                             .Where(p => p.Id != current.Id)
                             .OrderBy(p =>
                             {
                                 try { return p.StartTime; }
                                 catch { return DateTime.MaxValue; }
                             }))
                {
                    using (candidate)
                    {
                        if (AllowSetForegroundWindow(candidate.Id)) return;
                    }
                }
            }
            catch { }
        }

        // ============================================================
        // Crash handling
        // ============================================================
        //
        // NOTE: AccessViolationException is not catchable on .NET 4.8 without
        // [HandleProcessCorruptedStateExceptions], which we deliberately omit.

        private int _recoverableCrashDialogPending;

        private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // Let WPF unwind the failing input/layout callback before creating another window.
            // Opening a modal dialog inside this event re-enters the dispatcher while its visual
            // tree may still be inconsistent, which can turn one recoverable error into a loop.
            e.Handled = true;
            var logPath = CrashReporter.Capture(e.Exception, "Dispatcher");
            QueueRecoverableCrashDialog(e.Exception, logPath);
        }

        private void OnDomainException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception
                     ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown error");
            var logPath = CrashReporter.Capture(ex, "AppDomain");

            try
            {
                if (Dispatcher != null && !Dispatcher.HasShutdownStarted)
                    Dispatcher.Invoke(() => ShowCrashDialog(ex, logPath, isFatal: true));
                else
                    ShowCrashDialog(ex, logPath, isFatal: true);
            }
            catch { /* at least the log was written */ }

            CleanupSessionTemps();
            // CLR will terminate the process after this handler returns.
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            // An unobserved task is reported later by the finalizer, after the task and its caller
            // are already gone. It is not an active UI crash and there is no operation left for a
            // Retry/Continue dialog to recover. 1.7.5 surfaced the wrapper AggregateException as a
            // crash with no useful stack, which is both alarming and diagnostically empty. Keep the
            // complete flattened failure in the log, mark it observed, and leave the running app
            // alone. Live dispatcher and AppDomain failures still use the normal crash dialog.
            e.SetObserved();
            CrashReporter.Capture(e.Exception.Flatten(), "TaskScheduler (observed background fault)");
        }

        private void QueueRecoverableCrashDialog(Exception exception, string logPath)
        {
            if (System.Threading.Interlocked.Exchange(ref _recoverableCrashDialogPending, 1) != 0)
                return;
            try
            {
                if (Dispatcher != null && !Dispatcher.HasShutdownStarted)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                    {
                        try
                        {
                            if (!ShowCrashDialog(exception, logPath, isFatal: false))
                            {
                                CleanupSessionTemps();
                                Shutdown(1);
                            }
                        }
                        finally
                        {
                            System.Threading.Interlocked.Exchange(ref _recoverableCrashDialogPending, 0);
                        }
                    }));
                    return;
                }
            }
            catch { /* the error was still captured even if the dispatcher is unavailable */ }

            System.Threading.Interlocked.Exchange(ref _recoverableCrashDialogPending, 0);
        }

        /// <summary>
        /// Dark-themed crash report dialog. Returns true if the user chose Continue.
        /// Must be called on the UI thread.
        /// </summary>
        private static string CrashText(string key, string fallback) =>
            System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;

        private static void EnsureCrashPreviewGrain()
        {
            const int size = 256;
            var bitmap = new System.Windows.Media.Imaging.WriteableBitmap(
                size, size, 96, 96, PixelFormats.Bgra32, null);
            var pixels = new byte[size * size * 4];
            var random = new Random(1337);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (random.Next(3) != 0) continue;
                byte value = random.Next(2) == 0
                    ? (byte)random.Next(190, 255)
                    : (byte)random.Next(0, 50);
                pixels[i] = value;
                pixels[i + 1] = value;
                pixels[i + 2] = value;
                pixels[i + 3] = (byte)random.Next(35, 95);
            }
            bitmap.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
            var source = System.Windows.Media.Imaging.BitmapFrame.Create(bitmap);
            source.Freeze();
            var tile = new ImageBrush(source)
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, size, size),
                Stretch = Stretch.None
            };
            tile.Freeze();
            Current.Resources["GrainTileBrush"] = tile;
        }

        private static bool ShowCrashDialog(Exception ex, string logPath, bool isFatal)
        {
            bool shouldContinue = false;
            var win = CreateCrashDialog(ex, logPath, isFatal, result => shouldContinue = result);
            win.ShowDialog();
            return shouldContinue;
        }

        private static Window CreateCrashDialog(Exception ex, string logPath, bool isFatal, Action<bool> complete)
        {
            Window? owner = Current?.MainWindow is { IsVisible: true } main ? main : null;
            Brush pane = UiKit.Brush("PaneBrush", new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a)));
            Brush code = UiKit.Brush("MenuBackgroundBrush", new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x12)));
            Brush border = UiKit.Brush("CardBorderBrush", new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x3a)));
            Brush text = UiKit.Brush("TextBrush", Brushes.White);
            Brush muted = UiKit.Brush("MutedTextBrush", new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xaa)));
            Brush dim = UiKit.Brush("DimTextBrush", new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)));
            Brush danger = UiKit.Brush("DangerRed", new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44)));
            Brush primary = UiKit.Brush("PrimaryBrush", new SolidColorBrush(Color.FromRgb(0x1e, 0xa5, 0x4c)));
            Brush primaryHover = UiKit.Brush("PrimaryHoverBrush", primary);
            Brush onPrimary = UiKit.Brush("OnPrimaryBrush", new SolidColorBrush(Color.FromRgb(0x0a, 0x0a, 0x0a)));
            Brush button = UiKit.Brush("SurfaceBrush", new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)));
            Brush buttonHover = UiKit.Brush("SurfaceHoverBrush", new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)));
            Brush grain = UiKit.Brush("GrainTileBrush", Brushes.Transparent);
            double grainOpacity = Current?.TryFindResource("GrainOpacity") is double opacity
                ? opacity : 0.05;
            var paneShadow = Current?.TryFindResource("PaneShadowEffect")
                as System.Windows.Media.Effects.Effect;
            var quitNormal = new SolidColorBrush(Color.FromRgb(0x5a, 0x10, 0x10));
            var quitHover = new SolidColorBrush(Color.FromRgb(0xc4, 0x2b, 0x1c));
            string title = CrashText("Str_Crash_Title", "PewDF - Unexpected Error");
            var win = new Window
            {
                Title = title,
                Width = 760,
                Height = 540,
                MinWidth = 620,
                MinHeight = 430,
                ShowInTaskbar = owner is null
            };
            DialogChrome.Configure(win, owner, resizable: true, fade: false);
            win.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Controls/PickerStyles.xaml")
            });
            // Use the family scroll host, including its transparent scrollbar intersection.
            win.Resources[typeof(ScrollViewer)] = win.FindResource("BareScrollViewer");

            var root = new Grid { Background = Brushes.Transparent };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var summary = new Controls.PickerBorder
            {
                Background = pane,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                CornerRadius = UiKit.RadCard
            };
            var summaryGrid = new Grid { Margin = new Thickness(18, 16, 18, 15) };
            summaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            summaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            summaryGrid.Children.Add(new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(16),
                BorderBrush = danger,
                BorderThickness = new Thickness(1.5),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = "!",
                    Foreground = danger,
                    FontFamily = UiKit.MonoFont,
                    FontSize = 20,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
            var summaryText = new StackPanel();
            summaryText.Children.Add(new TextBlock
            {
                Text = ex.GetType().Name,
                Foreground = danger,
                FontFamily = UiKit.MonoFont,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold
            });
            summaryText.Children.Add(new TextBlock
            {
                Text = ex.Message,
                Foreground = text,
                FontSize = 14,
                LineHeight = 20,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 7, 0, 0)
            });
            summaryText.Children.Add(new TextBlock
            {
                Text = string.Format(CrashText("Str_Crash_Log", "Log: {0}"), logPath),
                Foreground = dim,
                FontFamily = UiKit.MonoFont,
                FontSize = 10.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 10, 0, 0)
            });
            Grid.SetColumn(summaryText, 1);
            summaryGrid.Children.Add(summaryText);
            var summaryLayers = new Grid();
            summaryLayers.Children.Add(new Border
            {
                Background = grain,
                Opacity = grainOpacity,
                CornerRadius = UiKit.RadCard,
                IsHitTestVisible = false
            });
            summaryLayers.Children.Add(summaryGrid);
            summary.Child = summaryLayers;
            var summaryHost = new Grid { Margin = new Thickness(18, 8, 18, 12) };
            summaryHost.Children.Add(new Border
            {
                Background = pane,
                CornerRadius = UiKit.RadCard,
                IsHitTestVisible = false,
                Effect = paneShadow?.CloneCurrentValue()
            });
            summaryHost.Children.Add(summary);
            Grid.SetRow(summaryHost, 0);
            root.Children.Add(summaryHost);

            var traceBox = UiKit.Field();
            traceBox.Text = FormatExceptionChain(ex);
            traceBox.Background = Brushes.Transparent;
            traceBox.Foreground = muted;
            traceBox.FontFamily = UiKit.MonoFont;
            traceBox.FontSize = 10.5;
            traceBox.IsReadOnly = true;
            traceBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            traceBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            traceBox.TextWrapping = TextWrapping.NoWrap;
            traceBox.BorderThickness = new Thickness(0);
            traceBox.Padding = new Thickness(12, 10, 12, 10);
            var traceLayers = new Grid();
            traceLayers.Children.Add(new Border
            {
                Background = grain,
                Opacity = grainOpacity,
                CornerRadius = UiKit.RadCard,
                IsHitTestVisible = false
            });
            traceLayers.Children.Add(traceBox);
            var traceCard = new Controls.PickerBorder
            {
                Background = code,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                CornerRadius = UiKit.RadCard,
                Child = traceLayers
            };
            var traceHost = new Grid { Margin = new Thickness(18, 0, 18, 14) };
            traceHost.Children.Add(new Border
            {
                Background = code,
                CornerRadius = UiKit.RadCard,
                IsHitTestVisible = false,
                Effect = paneShadow?.CloneCurrentValue()
            });
            traceHost.Children.Add(traceCard);
            Grid.SetRow(traceHost, 1);
            root.Children.Add(traceHost);

            var footer = new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = border,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(18, 11, 18, 11)
            };
            var btnPanel = new DockPanel { LastChildFill = false };
            var leftBtns = new StackPanel { Orientation = Orientation.Horizontal };
            var copyBtn = MakeCrashButton(CrashText("Str_Crash_Copy", "Copy Report"), button, buttonHover, text, 100);
            copyBtn.Click += (_, _) =>
            {
                try { Clipboard.SetText(BuildFullCrashReport(ex)); } catch { }
            };
            leftBtns.Children.Add(copyBtn);
            var logsBtn = MakeCrashButton(CrashText("Str_Crash_OpenLogs", "Open Logs"), button, buttonHover, text, 88);
            logsBtn.Margin = new Thickness(8, 0, 0, 0);
            logsBtn.Click += (_, _) =>
            {
                try
                {
                    Directory.CreateDirectory(CrashReporter.LogDir);
                    Process.Start(new ProcessStartInfo(CrashReporter.LogDir) { UseShellExecute = true });
                }
                catch { }
            };
            leftBtns.Children.Add(logsBtn);
            var githubBtn = MakeCrashButton(CrashText("Str_Crash_Report", "Report on GitHub"), button, buttonHover,
                new SolidColorBrush(Color.FromRgb(0x60, 0xc0, 0xff)), 128);
            githubBtn.Margin = new Thickness(8, 0, 0, 0);
            githubBtn.Click += (_, _) =>
            {
                try
                {
                    var msgLen = Math.Min(80, ex.Message.Length);
                    var title  = Uri.EscapeDataString(
                        $"Crash: {ex.GetType().Name}: {ex.Message[..msgLen]}");
                    var stack  = ex.StackTrace?.Length > 800
                        ? ex.StackTrace[..800] + "\n... (truncated)"
                        : ex.StackTrace ?? "(no stack trace)";
                    var body = Uri.EscapeDataString(
                        $"**Version:** {AppVersion.Display}\n" +
                        $"**OS:** {Environment.OSVersion}\n" +
                        $"**Exception:** `{ex.GetType().FullName}`\n" +
                        $"**Message:** {ex.Message}\n\n" +
                        $"```\n{stack}\n```\n\n" +
                        $"_Log folder: `{CrashReporter.LogDir}`_");
                    // SEMANTIC ANCHOR: KPDF_CRASH_REPORT_DESTINATION
                    // Crash reports must open the PewDF fork's issue tracker, not the upstream repository.
                    Process.Start(new ProcessStartInfo(
                        $"https://github.com/UncleRiot/KPDF/issues/new?title={title}&body={body}")
                        { UseShellExecute = true });
                }
                catch { }
            };
            leftBtns.Children.Add(githubBtn);
            DockPanel.SetDock(leftBtns, Dock.Left);
            btnPanel.Children.Add(leftBtns);
            var rightBtns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var contBtn = MakeCrashButton(CrashText("Str_Crash_Continue", "Continue"), primary, primaryHover,
                onPrimary, 88);
            contBtn.IsEnabled = !isFatal;
            contBtn.FontWeight = isFatal ? FontWeights.Normal : FontWeights.SemiBold;
            contBtn.Margin = new Thickness(0, 0, 8, 0);
            contBtn.Click += (_, _) => { complete(true); win.Close(); };
            var quitBtnCtrl = MakeCrashButton(CrashText("Str_Crash_Quit", "Quit"), quitNormal, quitHover, Brushes.White, 72);
            quitBtnCtrl.FontWeight = isFatal ? FontWeights.SemiBold : FontWeights.Normal;
            quitBtnCtrl.Click += (_, _) => { complete(false); win.Close(); };
            rightBtns.Children.Add(contBtn);
            rightBtns.Children.Add(quitBtnCtrl);
            DockPanel.SetDock(rightBtns, Dock.Right);
            btnPanel.Children.Add(rightBtns);
            footer.Child = btnPanel;
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            win.Content = DialogChrome.Frame(win, owner, title,
                () => { complete(false); win.Close(); }, root,
                new Thickness(20, 20, 20, 24));
            return win;
        }

        private static Button MakeTitleBarCloseButton(SolidColorBrush fg, SolidColorBrush hoverBg)
        {
            var t  = new ControlTemplate(typeof(Button));
            var b  = new FrameworkElementFactory(typeof(Border));
            b.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.TemplatedParent)
                });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty,   VerticalAlignment.Center);
            b.AppendChild(cp);
            t.VisualTree = b;

            var s    = new Style(typeof(Button));
            s.Setters.Add(new Setter(Button.BackgroundProperty, Brushes.Transparent));
            s.Setters.Add(new Setter(Button.ForegroundProperty, fg));
            s.Setters.Add(new Setter(Button.TemplateProperty,   t));
            var trig = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            trig.Setters.Add(new Setter(Button.BackgroundProperty, hoverBg));
            trig.Setters.Add(new Setter(Button.ForegroundProperty, Brushes.White));
            s.Triggers.Add(trig);

            return new Button
            {
                Content                  = "",
                FontFamily               = new FontFamily("Segoe MDL2 Assets"),
                FontSize                 = 11,
                Width                    = 46,
                BorderThickness          = new Thickness(0),
                VerticalAlignment        = VerticalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor                   = Cursors.Arrow,
                Style                    = s
            };
        }

        private static Button MakeCrashButton(string label,
            Brush normal, Brush hover, Brush fg,
            double width = 88)
        {
            var btn = UiKit.Make(label, normal, hover, fg, fg);
            btn.Width  = width;
            btn.Height = 28;
            return btn;
        }

        private static string FormatExceptionChain(Exception ex)
        {
            var sb    = new StringBuilder();
            var inner = ex;
            var depth = 0;
            while (inner != null && depth < 5)
            {
                if (depth > 0) { sb.AppendLine(); sb.AppendLine("=== Inner Exception ==="); }
                sb.AppendLine($"{inner.GetType().FullName}: {inner.Message}");
                sb.AppendLine(inner.StackTrace ?? "(no stack trace)");
                inner = inner.InnerException;
                depth++;
            }
            return sb.ToString().TrimEnd();
        }

        private static string BuildFullCrashReport(Exception ex)
        {
            var sb  = new StringBuilder();
            sb.AppendLine($"PewDF v{AppVersion.Display}");
            sb.AppendLine($"Time : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"OS   : {Environment.OSVersion}");
            sb.AppendLine();
            sb.Append(FormatExceptionChain(ex));
            return sb.ToString();
        }

        // ============================================================
        // Temp file tracking
        // ============================================================

        /// <summary>
        /// User-private temp directory for session working files (encrypted PDFs, etc.).
        /// %LOCALAPPDATA% is user-private and not indexed by Windows Search.
        /// </summary>
        private static readonly string UserTempDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PewDF", "Temp");
        internal static string TempDir => IsStartupSmoke ? Path.Combine(SmokeDataRoot, "Temp") : UserTempDir;

        private static readonly List<string> _sessionTemps = [];

        /// <summary>
        /// Creates a tracked temp path of the form pewdf_&lt;tag&gt;_&lt;guid&gt;.pdf
        /// under %LOCALAPPDATA%\PewDF\Temp\.
        /// All registered paths are deleted when CleanupSessionTemps() is called.
        /// </summary>
        internal static string MakeTempFile(string tag)
        {
            try { Directory.CreateDirectory(TempDir); } catch { }
            // SEMANTIC ANCHOR: PEWDF_TEMP_NAME_SSOT: Only PewDF-owned temporary files are created and cleaned.
            var path = Path.Combine(TempDir, $"pewdf_{tag}_{Guid.NewGuid():N}.pdf");
            lock (_sessionTemps) _sessionTemps.Add(path);
            return path;
        }

        /// <summary>Deletes all temp files registered this session (best-effort).</summary>
        internal static void CleanupSessionTemps()
        {
            lock (_sessionTemps)
            {
                foreach (var f in _sessionTemps)
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
                _sessionTemps.Clear();
            }
        }

        /// <summary>
        /// Deletes only PewDF-owned temporary PDFs from the application temp directory.
        /// Locked files (still open by another instance) are silently skipped.
        /// </summary>
        internal static void CleanupStaleTemps()
        {
            // Current location
            try
            {
                if (Directory.Exists(TempDir))
                    foreach (var f in Directory.GetFiles(TempDir, "pewdf_*.pdf"))
                        try { File.Delete(f); } catch { }
            }
            catch { }
        }

        internal static string? GetSetting(string name)
        {
            if (IsStartupSmoke) return SmokeSettings.GetValueOrDefault(name);
            if (Services.AppDataPaths.PortableRoot is not null)
                return Services.AppDataPaths.GetPortableSetting(name);
            try
            {
                // SEMANTIC ANCHOR: PEWDF_SETTINGS_IDENTITY_SSOT: Read only PewDF-owned settings.
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\PewDF\Settings");
                return key?.GetValue(name) as string;
            }
            catch { return null; }
        }

        internal static void SetSetting(string name, string value)
        {
            if (IsStartupSmoke) { SmokeSettings[name] = value; return; }
            if (Services.AppDataPaths.PortableRoot is not null)
            {
                try { Services.AppDataPaths.SetPortableSetting(name, value); } catch { }
                return;
            }
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\PewDF\Settings");
                key.SetValue(name, value);
            }
            catch { /* best-effort */ }
        }

        internal static void RemoveSetting(string name)
        {
            if (IsStartupSmoke) { SmokeSettings.Remove(name); return; }
            if (Services.AppDataPaths.PortableRoot is not null)
            {
                try { Services.AppDataPaths.RemovePortableSetting(name); } catch { }
                return;
            }
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\PewDF\Settings", writable: true);
                key?.DeleteValue(name, throwOnMissingValue: false);
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Wipes all persisted PewDF state: settings (registry), downloaded OCR language packs, the
        /// native OCR cache, and temp files. Best-effort - files locked this session (e.g. loaded native
        /// DLLs) are skipped and clear on the next restart. The user's actual PDFs are never touched.
        /// </summary>
        // SEMANTIC ANCHOR: SETTINGS_RESET_PREFERENCES_ONLY
        // Unlike ClearAllData, reset must not delete signed PDFs, signatures, OCR packs or temp files.
        internal static void ResetPreferences()
        {
            if (IsStartupSmoke) { SmokeSettings.Clear(); return; }
            // SEMANTIC ANCHOR: SETTINGS_RESET_RIBBON_LAYOUT_INVARIANT
            // User-customized ribbon arrangement is a preference, not user content.
            // Reset must remove its override using the same path as ribbon save/load (SSOT).
            try { File.Delete(global::PewDF.MainWindow.PreviewLayoutFile); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (Services.AppDataPaths.PortableRoot is not null)
            {
                try { System.IO.File.Delete(Services.AppDataPaths.SettingsFile); } catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            else
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\PewDF\Settings", false); }
                catch (System.Security.SecurityException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        internal static void ClearAllData()
        {
            if (Services.AppDataPaths.PortableRoot is not null)
                TryDeleteDir(Services.AppDataPaths.UserRoot);
            else
                try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\PewDF\Settings", throwOnMissingSubKey: false); } catch { }

            TryDeleteDir(Services.AppDataPaths.TessDataDirectory);
            TryDeleteDir(Path.Combine(Services.AppDataPaths.LocalRoot, "ocr"));
            TryDeleteDir(TempDir);                              // temp working files

        }

        private static void TryDeleteDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // A whole-folder delete fails if any file is locked; remove what we can so the rest clears now.
                try
                {
                    foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                        try { File.Delete(f); } catch { }
                }
                catch { }
            }
        }

        // ── Recent files (most-recent first, capped) ─────────────────────
        private const int RecentFilesMax = 10;
        // "1" = don't track recently opened files at all (#146, privacy on shared machines).
        internal const string NoRecentFilesSetting = "NoRecentFiles";

        internal static System.Collections.Generic.List<string> GetRecentFiles()
        {
            var list = new System.Collections.Generic.List<string>();
            var raw = GetSetting("RecentFiles");
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (var p in raw!.Split('|'))
                if (!string.IsNullOrWhiteSpace(p)) list.Add(p);
            return list;
        }

        internal static void AddRecentFile(string path)
        {
            if (GetSetting(NoRecentFilesSetting) == "1") return;   // tracking disabled (#146)
            if (string.IsNullOrWhiteSpace(path)) return;
            var list = GetRecentFiles();
            list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, path);
            while (list.Count > RecentFilesMax) list.RemoveAt(list.Count - 1);
            SetSetting("RecentFiles", string.Join("|", list));   // '|' is illegal in Windows paths
        }

        internal static void ClearRecentFiles() => RemoveSetting("RecentFiles");

        // Drops a single entry from the recent-files list (used by the per-row remove button).
        internal static void RemoveRecentFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var list = GetRecentFiles();
            list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (list.Count == 0) RemoveSetting("RecentFiles");
            else SetSetting("RecentFiles", string.Join("|", list));
        }

        // ============================================================
        // Security - Authenticode verification + pdfium integrity
        // ============================================================

        // ── WinVerifyTrust P/Invoke ──────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_FILE_INFO
        {
            public uint   cbStruct;
            public IntPtr pcwszFilePath;   // LPCWSTR
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint   cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint   dwUIChoice;          // 2 = WTD_UI_NONE
            public uint   fdwRevocationChecks; // 0 = WTD_REVOKE_NONE
            public uint   dwUnionChoice;       // 1 = WTD_CHOICE_FILE
            public IntPtr pUnion;              // → WINTRUST_FILE_INFO
            public uint   dwStateAction;       // 0 = WTD_STATEACTION_IGNORE
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint   dwProvFlags;         // 0x1000 = WTD_CACHE_ONLY_URL_RETRIEVAL
            public uint   dwUIContext;
            public IntPtr pSignatureSettings;
        }

        private static readonly Guid WTD_VERIFY_GENERIC =
            new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [LibraryImport("wintrust.dll")]
        private static partial uint WinVerifyTrust(
            IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);

        // ── Public helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Calls WinVerifyTrust to validate an Authenticode signature.
        /// Returns (Valid, SubjectCN, Thumbprint).
        /// Valid=false for unsigned, expired (past grace), or tampered files.
        /// </summary>
        internal static (bool Valid, string Subject, string Thumbprint)
            VerifyAuthenticode(string filePath)
        {
            var subject    = "(not signed)";
            var thumbprint = string.Empty;

            // Try to read cert info regardless of signature validity
            try
            {
#pragma warning disable SYSLIB0057 // Required to extract the signer certificate from a PE Authenticode signature.
                var raw = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
                using var cert = X509CertificateLoader.LoadCertificate(raw.GetRawCertData());
                subject    = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
                thumbprint = cert.Thumbprint ?? string.Empty;
            }
            catch { /* unsigned or unreadable */ }

            // Full chain + revocation check via WinVerifyTrust
            var pathPtr      = Marshal.StringToHGlobalUni(filePath);
            var fileInfoPtr  = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            var dataPtr      = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
            try
            {
                Marshal.StructureToPtr(new WINTRUST_FILE_INFO
                {
                    cbStruct      = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                    pcwszFilePath = pathPtr
                }, fileInfoPtr, false);

                Marshal.StructureToPtr(new WINTRUST_DATA
                {
                    cbStruct      = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice    = 2,  // WTD_UI_NONE
                    dwUnionChoice = 1,  // WTD_CHOICE_FILE
                    pUnion        = fileInfoPtr,
                    dwProvFlags   = 0x1000   // WTD_CACHE_ONLY_URL_RETRIEVAL, never hit the network
                }, dataPtr, false);

                var actionId = WTD_VERIFY_GENERIC;
                uint hr = WinVerifyTrust(IntPtr.Zero, ref actionId, dataPtr);
                return (hr == 0, subject, thumbprint);
            }
            finally
            {
                Marshal.FreeHGlobal(dataPtr);
                Marshal.FreeHGlobal(fileInfoPtr);
                Marshal.FreeHGlobal(pathPtr);
            }
        }

        /// <summary>
        /// Convenience wrapper: verify the currently running EXE.
        /// </summary>
        internal static (bool Valid, string Subject, string Thumbprint) GetExeSignerInfo()
        {
            try
            {
                return VerifyAuthenticode(Environment.ProcessPath
                    ?? throw new InvalidOperationException("The current executable path is unavailable."));
            }
            catch
            {
                return (false, "(not signed)", string.Empty);
            }
        }

        /// <summary>SHA256 hex of the currently running EXE (for the About dialog).</summary>
        internal static string GetExeSha256()
        {
            try
            {
                var path = Environment.ProcessPath
                    ?? throw new InvalidOperationException("The current executable path is unavailable.");
                using var fs  = File.OpenRead(path);
                return Convert.ToHexString(SHA256.HashData(fs));
            }
            catch { return "(unavailable)"; }
        }

        // ── About dialog ────────────────────────────────────────────────────────

        internal static void ShowAboutDialog(Window owner)
        {
            // Gather info on a background thread so the UI isn't blocked by hashing
            var version    = AppVersion.Display;
            var (sigValid, sigSubject, sigThumbprint) = GetExeSignerInfo();
            var sha256 = GetExeSha256();

            // ── Layout ──────────────────────────────────────────────────────
            var bg     = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));
            var bgCard = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a));
            var fg     = new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0));
            var fgDim  = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));
            var accent = new SolidColorBrush(Color.FromRgb(0x1e, 0xa5, 0x4c));
            var mono   = new FontFamily("Consolas");

            // Title bar
            var titleBar = new Grid
            {
                Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
                Height = 32
            };
            titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleText = new TextBlock
            {
                Text = (Current.TryFindResource("Str_TT_About") as string ?? AppName)
                    .Replace(" (F12)", string.Empty, StringComparison.Ordinal),
                Foreground = fg, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0), FontSize = 13, FontWeight = FontWeights.SemiBold
            };
            Grid.SetColumn(titleText, 0);
            titleBar.Children.Add(titleText);

            Window? dlg = null;
            var closeBtn = MakeTitleBarCloseButton(
                new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
                new SolidColorBrush(Color.FromRgb(0xc0, 0x3b, 0x3b)));
            closeBtn.Click += (_, __) => dlg!.Close();
            Grid.SetColumn(closeBtn, 1);
            titleBar.Children.Add(closeBtn);

            // Helper: labeled row (onClick makes the value a clickable hyperlink)
            static StackPanel MakeRow(string label, string value,
                SolidColorBrush labelBrush, SolidColorBrush valueBrush,
                FontFamily? valueFont = null, bool wrap = false, Action? onClick = null)
            {
                var sp = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 10) };
                sp.Children.Add(new TextBlock
                {
                    Text = label, Foreground = labelBrush,
                    FontSize = 10, Margin = new Thickness(0, 0, 0, 2)
                });
                if (onClick != null)
                {
                    var tb = new TextBlock
                    {
                        FontFamily = valueFont ?? new FontFamily("Segoe UI"),
                        FontSize = 12,
                        Cursor = Cursors.Hand
                    };
                    var hl = new Hyperlink(new Run(value))
                    {
                        Foreground = valueBrush,
                        TextDecorations = null
                    };
                    hl.Click += (_, _) => onClick();
                    tb.Inlines.Add(hl);
                    sp.Children.Add(tb);
                }
                else
                {
                    sp.Children.Add(new TextBlock
                    {
                        Text = value, Foreground = valueBrush,
                        FontFamily = valueFont ?? new FontFamily("Segoe UI"),
                        FontSize = 12,
                        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
                    });
                }
                return sp;
            }

            static void OpenUrl(string url)
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            }

            var none = Current.TryFindResource("Str_Margin_None") as string ?? "None";
            var sigInfo = sigValid ? $"{sigSubject}" : none;

            var thumbInfo = string.IsNullOrEmpty(sigThumbprint) ? none : sigThumbprint;

            var card = new Border
            {
                Background = bgCard,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 0, 0, 0)
            };
            var cardContent = new StackPanel();
            cardContent.Children.Add(MakeRow(Current.TryFindResource("Str_About_Version") as string ?? string.Empty, $"v{version}", fgDim, accent,
                onClick: () => OpenUrl($"https://github.com/UncleRiot/KPDF/releases/tag/v{version}")));
            cardContent.Children.Add(MakeRow(Current.TryFindResource("Str_About_Publisher") as string ?? string.Empty, sigInfo, fgDim, fg));
            cardContent.Children.Add(MakeRow(Current.TryFindResource("Str_About_Thumbprint") as string ?? string.Empty, thumbInfo, fgDim, fg, mono, wrap: true));
            cardContent.Children.Add(MakeRow(Current.TryFindResource("Str_About_ExeSha") as string ?? string.Empty, sha256, fgDim, fg, mono, wrap: true));
            card.Child = cardContent;

            // Close button
            var okBtn = UiKit.Make(Current.TryFindResource("Str_Lbl_Close") as string ?? string.Empty,
                new SolidColorBrush(Color.FromRgb(0x1e, 0xa5, 0x4c)),
                new SolidColorBrush(Color.FromRgb(0x17, 0x7a, 0x38)),
                new SolidColorBrush(Colors.White),
                new SolidColorBrush(Colors.White));
            okBtn.Width  = 80;
            okBtn.Height = 28;
            okBtn.HorizontalAlignment = HorizontalAlignment.Right;
            okBtn.Margin = new Thickness(0, 12, 0, 0);
            okBtn.Click += (_, __) => dlg!.Close();

            // PewDF logo - clickable link to product site
            var logo = new TextBlock { FontSize = 22, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) };
            var logoHl = new Hyperlink(new Run("PewDF"))
            {
                Foreground = accent,
                TextDecorations = null
            };
            logoHl.Click += (_, _) => OpenUrl("https://pdf.killertools.com");
            logo.Inlines.Add(logoHl);

            // Tagline with Killer Tools link
            var tagline = new TextBlock { FontSize = 11, Margin = new Thickness(0, 0, 0, 16) };
            var taglineText = Current.TryFindResource("Str_Tagline") as string ?? string.Empty;
            int brandPosition = taglineText.IndexOf("{0}", StringComparison.Ordinal);
            tagline.Inlines.Add(new Run(brandPosition >= 0 ? taglineText[..brandPosition] : taglineText) { Foreground = fgDim });
            var ktHl = new Hyperlink(new Run("Killer Tools"))
            {
                Foreground = accent,
                TextDecorations = null
            };
            ktHl.Click += (_, _) => OpenUrl("https://killertools.net");
            tagline.Inlines.Add(ktHl);
            tagline.Inlines.Add(new Run(brandPosition >= 0 ? taglineText[(brandPosition + 3)..] : string.Empty) { Foreground = fgDim });

            var body = new StackPanel { Margin = new Thickness(16, 16, 16, 20) };
            body.Children.Add(logo);
            body.Children.Add(tagline);
            body.Children.Add(card);
            body.Children.Add(okBtn);

            var root = new DockPanel();
            DockPanel.SetDock(titleBar, Dock.Top);
            root.Children.Add(titleBar);
            root.Children.Add(body);
            root.Background = bg;

            // Make title bar draggable
            titleBar.MouseLeftButtonDown += (_, me) =>
            {
                if (me.ButtonState == MouseButtonState.Pressed) dlg!.DragMove();
            };

            dlg = new Window
            {
                Content = root,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.Height,
                Width = 540,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = bg
            };

            dlg.ShowDialog();
        }


        // SEMANTIC ANCHOR: PORTABLE_ONLY_NO_INSTALLER_INVARIANT:
        // Installation, uninstall, shortcuts and install registry metadata are not owned by the portable app.

    }
}
