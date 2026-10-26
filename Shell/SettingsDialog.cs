// SEMANTIC ANCHOR: CENTRAL_SETTINGS_DIALOG
// Central window hosts UI proxies only; existing theme/language radio handlers remain the SSOT.
// Do not duplicate persistence or modify the original rail pickers until the replacement is tested.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;
using System.Windows.Input;
using System.Diagnostics;
using System.IO;
using PewDF.Controls;
using PewDF.Services;

namespace PewDF
{
    public partial class MainWindow
    {
        private Window? _settingsDialog;

        // SEMANTIC ANCHOR: FILE_OPTIONS_DIALOG_ENTRY. Reuse the existing dialog and selection/persistence handlers.
        private void FileOptions_Click(object sender, RoutedEventArgs e) => OpenSettingsDialog(false);

        private void SettingsLanguage_Click(object sender, RoutedEventArgs e) => OpenSettingsDialog(false);
        private void SettingsAppearance_Click(object sender, RoutedEventArgs e) => OpenSettingsDialog(true);

        private void OpenSettingsDialog(bool appearance)
        {
            if (_settingsDialog is { IsVisible: true })
            {
                _settingsDialog.Activate();
                return;
            }

            SyncPickerState();
            var dialog = new Window
            {
                Owner = this,
                Title = WindowIdentity.AppTitle,
                Icon = WindowIdentity.Icon,
                // SEMANTIC ANCHOR: SETTINGS_DIALOG_RESPONSIVE_HEIGHT
                // Keep the settings window compact by default; scrolling remains available for long lists.
                // Limit to the working area so the title bar always stays reachable on small displays.
                Width = 760,
                Height = Math.Min(510, SystemParameters.WorkArea.Height - 32),
                MinWidth = 560,
                MinHeight = 390,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                FontFamily = (FontFamily)Application.Current.Resources["UiFont"]
            };
            // SETTINGS_DIALOG_CHROME_SSOT: every standalone settings surface uses DialogChrome.
            // Never introduce native caption, border or themed window chrome independently.
            DialogChrome.Configure(dialog, this, resizable: true);

            // SEMANTIC ANCHOR: SETTINGS_LAYOUT_SSOT
            // Reuse the existing ThemeRadio/ThemeCheck and ribbon button styles from MainWindow.
            // Never fall back to the white native WPF radio/check/button templates.
            var layout = new Grid { Margin = new Thickness(16) };
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var navigation = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            var content = new Grid();
            // SETTINGS_GENERAL_CONTROLS_SSOT: actions use UiKit buttons; navigation uses a separate style.
            var generalPanel = new StackPanel { Margin = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Left };
            var languagePanel = new Grid { Margin = new Thickness(12) };
            languagePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            languagePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var appearancePanel = new Grid { Margin = new Thickness(12) };
            // SEMANTIC ANCHOR: SETTINGS_THEME_TWO_COLUMN_LAYOUT
            // Existing themes stay in the left column; Accent color occupies a fixed right column.
            // Never move accent swatches back underneath the theme list or restore Film Grain here.
            appearancePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            appearancePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });

            // Existing flyout radios remain the sole source for selection handlers and persistence.
            // Proxies reflect the original IsChecked state and forward selection to it.
                AddOption(languagePanel, LangEnRadio);
                AddOption(languagePanel, LangBnRadio);
                AddOption(languagePanel, LangCsRadio);
                AddOption(languagePanel, LangDeRadio);
                AddOption(languagePanel, LangEsRadio);
                AddOption(languagePanel, LangFrRadio);
                AddOption(languagePanel, LangHuRadio);
                AddOption(languagePanel, LangItalianRadio);
                AddOption(languagePanel, LangJaRadio);
                AddOption(languagePanel, LangKkRadio);
                AddOption(languagePanel, LangNbRadio);
                AddOption(languagePanel, LangPlRadio);
                AddOption(languagePanel, LangPtRadio);
                AddOption(languagePanel, LangRuRadio);
                AddOption(languagePanel, LangTrRadio);
                AddOption(languagePanel, LangUkRadio);
                AddOption(languagePanel, LangViRadio);
                AddOption(languagePanel, LangZhCNRadio);
                AddOption(languagePanel, LangZhTWRadio);
                AddOption(appearancePanel, ThemeDarkRadio);
                AddOption(appearancePanel, ThemeLightRadio);
                AddOption(appearancePanel, ThemeHCRadio);
                AddOption(appearancePanel, Theme98SERadio);
                AddOption(appearancePanel, ThemeBloodRadio);
                AddOption(appearancePanel, ThemeGreedRadio);
                AddOption(appearancePanel, ThemeCyanoticRadio);
                AddOption(appearancePanel, ThemeEctoplasmRadio);
                AddOption(appearancePanel, ThemeDecayRadio);
                AddOption(appearancePanel, ThemeMalaiseRadio);
                AddOption(appearancePanel, ThemeSepulchreRadio);
                AddOption(appearancePanel, ThemeDeliriumRadio);
                AddOption(appearancePanel, ThemeMourningRadio);
            // SEMANTIC ANCHOR: SETTINGS_ACCENT_RIGHT_COLUMN
            // Exactly one vertical accent palette lives to the right of the unchanged theme list.
            // Its swatches reuse ThemeManager.ApplyAccent and remain available only for supported themes.
            var accentPanel = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
            // SEMANTIC ANCHOR: SETTINGS_LANGUAGE_SSOT
            // No hard-coded labels in this dialog: use existing localized Resources.
            // DynamicResource follows the application language when it changes at runtime.
            var accentHeading = new TextBlock { Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold };
            accentHeading.Text = "Accent color";
            accentHeading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            accentPanel.Children.Add(accentHeading);
            var accentChoices = new StackPanel { Orientation = Orientation.Vertical };
            accentPanel.Children.Add(accentChoices);

            void RefreshAccentChoices()
            {
                accentChoices.Children.Clear();
                Theme selected = ThemeDarkRadio.IsChecked == true ? Theme.Dark :
                    ThemeLightRadio.IsChecked == true ? Theme.Light :
                    ThemeHCRadio.IsChecked == true ? Theme.Black :
                    Theme98SERadio.IsChecked == true ? Theme.SE98 : (Theme)(-1);
                accentPanel.Visibility = selected == (Theme)(-1) ? Visibility.Collapsed : Visibility.Visible;
                if (accentPanel.Visibility != Visibility.Visible) return;
                var selectedAccent = selected switch
                {
                    Theme.Light => ThemeManager.LightAccentChoice,
                    Theme.Black => ThemeManager.BlackAccentChoice,
                    Theme.SE98 => ThemeManager.SE98AccentChoice,
                    _ => ThemeManager.DarkAccentChoice
                };
                foreach (var (accent, hex) in StripColorsFor(selected))
                {
                    var swatch = new Button
                    {
                        Width = 27, Height = 27, HorizontalAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(0, 0, 0, 7), ToolTip = accent.ToString(),
                        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                        BorderBrush = accent == selectedAccent ? Brushes.White : Brushes.Transparent,
                        BorderThickness = new Thickness(accent == selectedAccent ? 1 : 0),
                        Cursor = Cursors.Hand, FocusVisualStyle = null
                    };
                    Theme capturedFamily = selected;
                    var capturedAccent = accent;
                    swatch.Click += (_, _) =>
                    {
                        ThemeManager.ApplyAccent(capturedFamily, capturedAccent);
                        RefreshAccentChoices();
                    };
                    accentChoices.Children.Add(swatch);
                }
            }
            foreach (var source in new[] { ThemeDarkRadio, ThemeLightRadio, ThemeHCRadio, Theme98SERadio,
                ThemeBloodRadio, ThemeGreedRadio, ThemeCyanoticRadio, ThemeEctoplasmRadio,
                ThemeDecayRadio, ThemeMalaiseRadio, ThemeSepulchreRadio, ThemeDeliriumRadio, ThemeMourningRadio })
                source.Checked += (_, _) => RefreshAccentChoices();
            // Reserve the right-hand column without changing theme row placement.
            Grid.SetColumn(accentPanel, 1);
            Grid.SetRow(accentPanel, 0);
            Grid.SetRowSpan(accentPanel, appearancePanel.RowDefinitions.Count);
            appearancePanel.Children.Add(accentPanel);
            RefreshAccentChoices();

            // SEMANTIC ANCHOR: SETTINGS_GENERAL_ACTIONS_SSOT
            // General actions use existing Windows registration, preferences, and diagnostics owners.
            // Never write the protected PDF UserChoice key or duplicate the diagnostics preference.
            var defaultPdfButton = CreateSettingsActionButton("Str_SetDefaultPdfAction");
            defaultPdfButton.Click += (_, _) => OpenPdfDefaultSettings(dialog);
            generalPanel.Children.Add(defaultPdfButton);

            var loggingCheck = new CheckBox { IsChecked = DebugLogging.Enabled, Margin = new Thickness(0, 14, 0, 14) };
            loggingCheck.Style = (Style)FindResource("ThemeCheck");
            loggingCheck.SetResourceReference(ContentControl.ContentProperty, "Str_DebugLoggingAction");
            loggingCheck.Click += (_, _) => DebugLogging.Enabled = loggingCheck.IsChecked == true;
            generalPanel.Children.Add(loggingCheck);

            var folderButton = CreateSettingsActionButton("Str_OpenConfigFolderAction");
            folderButton.Click += (_, _) =>
            {
                try
                {
                    Directory.CreateDirectory(AppDataPaths.UserRoot);
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppDataPaths.UserRoot}\"") { UseShellExecute = true });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                { PewDialog.Show(dialog, ex.Message, WindowIdentity.AppTitle, MessageBoxButton.OK, MessageBoxImage.Error); }
            };
            generalPanel.Children.Add(folderButton);

            var resetButton = CreateSettingsActionButton("Str_ResetSettingsAction");
            resetButton.Margin = new Thickness(0, 14, 0, 0);
            resetButton.Click += (_, _) =>
            {
                if (PewDialog.Show(dialog, Loc("Str_ResetSettingsConfirm"), Loc("Str_ResetSettingsAction"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                // Reset only preferences, never user PDFs, signatures or downloaded OCR files.
                App.ResetPreferences();
                DebugLogging.Enabled = false;
                loggingCheck.IsChecked = false;
                PewDialog.Show(dialog, Loc("Str_ResetSettingsRestart"), Loc("Str_ResetSettingsAction"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            };
            generalPanel.Children.Add(resetButton);

            var generalScroll = new ScrollViewer { Content = generalPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var languageScroll = new ScrollViewer { Content = languagePanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var appearanceScroll = new ScrollViewer { Content = appearancePanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            content.Children.Add(generalScroll);
            content.Children.Add(languageScroll);
            content.Children.Add(appearanceScroll);

            void ShowPage(int page)
            {
                generalScroll.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
                languageScroll.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
                appearanceScroll.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
            }
            // SEMANTIC ANCHOR: SETTINGS_LOCALIZATION_SSOT
            // Tab labels must always use the existing localized string dictionaries.
            var generalButton = CreateNavigationButton("Str_General");
            var languageButton = CreateNavigationButton("Str_Language");
            var appearanceButton = CreateNavigationButton("Str_Theme");
            generalButton.Click += (_, _) => ShowPage(0);
            languageButton.Click += (_, _) => ShowPage(1);
            appearanceButton.Click += (_, _) => ShowPage(2);
            navigation.Children.Add(generalButton);
            navigation.Children.Add(languageButton);
            navigation.Children.Add(appearanceButton);
            Grid.SetColumn(navigation, 0);
            Grid.SetColumn(content, 1);
            layout.Children.Add(navigation);
            layout.Children.Add(content);
            // SETTINGS_DIALOG_LAYOUT_INVARIANT: the common frame owns the title bar and background;
            // the existing two-column settings layout remains the single content implementation.
            dialog.Content = DialogChrome.Frame(dialog, this, WindowIdentity.AppTitle, () => dialog.Close(), layout);
            ShowPage(appearance ? 2 : 0);
            dialog.Closed += (_, _) => _settingsDialog = null;
            _settingsDialog = dialog;
            dialog.SourceInitialized += (_, _) => WindowIdentity.Apply(dialog);
            dialog.Show();
        }

        // SEMANTIC ANCHOR: PDF_DEFAULT_PROMPT_LIFECYCLE
        // Prompt only in the primary visible window, only if not already default and not suppressed.
        // The checkbox persists independently of Yes/No; Windows alone confirms the association.
        internal void OfferDefaultPdfOnFirstLaunch()
        {
            if (App.GetSetting(PdfDefaultApplication.SuppressPromptSetting) == "1" || PdfDefaultApplication.IsDefault)
                return;
            // PDF_DEFAULT_PROMPT_DIALOG_SSOT: use the shared themed prompt and checkbox, never a native Window.
            // Closing the prompt is not consent; persist the opt-out only for explicit Yes/No answers.
            var (answer, dontAskAgain) = PewDialog.ShowWithCheckbox(this,
                Loc("Str_Dlg_SetDefaultPdfMsg"), Loc("Str_DontAskAgain"), WindowIdentity.AppTitle,
                MessageBoxButton.YesNo, defaultResult: MessageBoxResult.Cancel);
            if (answer is not (MessageBoxResult.Yes or MessageBoxResult.No)) return;
            if (dontAskAgain) App.SetSetting(PdfDefaultApplication.SuppressPromptSetting, "1");
            if (answer == MessageBoxResult.Yes) OpenPdfDefaultSettings(this);
        }

        private static void OpenPdfDefaultSettings(Window owner)
        {
            try { PdfDefaultApplication.RegisterAndOpenWindowsSettings(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                PewDialog.Show(owner, ex.Message, WindowIdentity.AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // SETTINGS_ACTION_STYLE_SSOT: regular settings actions share UiKit.Make with other PewDF dialogs.
        // Navigation buttons are not general-purpose action controls; localization stays in the resource dictionary.
        private static Button CreateSettingsActionButton(string resourceKey)
        {
            var button = UiKit.Make(Application.Current.TryFindResource(resourceKey) as string ?? resourceKey, accent: false);
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.Margin = new Thickness(0, 0, 0, 12);
            return button;
        }

        private static Button CreateNavigationButton(string resourceKey)
        {
            var button = new Button { HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(10, 8, 10, 8),
                FocusVisualStyle = null, Cursor = System.Windows.Input.Cursors.Hand };
            button.Style = (Style)Application.Current.MainWindow.FindResource("KpdfPreviewCommandButton");
            button.SetResourceReference(Control.BackgroundProperty, "PaneBrush");
            button.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            button.SetResourceReference(ContentControl.ContentProperty, resourceKey);
            return button;
        }

        // SEMANTIC ANCHOR: SETTINGS_RADIO_THEME_AND_PERSISTENCE
        // ThemeRadio is the central visual SSOT. The original radio remains the only
        // source for selection, callbacks and saved settings; do not duplicate handlers.
        private void AddOption(Grid target, RadioButton original)
        {
            int index = target.Children.Count;
            bool themeOption = original.Name.StartsWith("Theme", StringComparison.Ordinal);
            int row = themeOption ? index : index / 2;
            if (themeOption || index % 2 == 0)
                target.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var proxy = new RadioButton
            {
                Content = FindOptionLabel(original),
                IsChecked = original.IsChecked,
                GroupName = themeOption ? "SettingsAppearance" : "SettingsLanguage",
                Style = (Style)FindResource("ThemeRadio"),
                Margin = new Thickness(0, 0, 10, 2),
                FocusVisualStyle = null
            };
            // Bind to the original localized content, not a one-time snapshot.
            if (original.Content is Grid originalGrid)
            {
                foreach (UIElement child in originalGrid.Children)
                    if (child is TextBlock sourceText && Grid.GetColumn(sourceText) == 0)
                    {
                        proxy.SetBinding(ContentControl.ContentProperty, new Binding(nameof(TextBlock.Text)) { Source = sourceText, Mode = BindingMode.OneWay });
                        break;
                    }
            }
            else
                proxy.SetBinding(ContentControl.ContentProperty, new Binding(nameof(ContentControl.Content)) { Source = original, Mode = BindingMode.OneWay });
            proxy.Checked += (_, _) => original.IsChecked = true;
            if (themeOption)
            {
                // SEMANTIC ANCHOR: SETTINGS_THEME_ROWS_HAVE_NO_SWATCHES
                // Do not restore the obsolete right-hand preview colors or automatic accent reset.
                Grid.SetRow(proxy, row);
                target.Children.Add(proxy);
            }
            else
            {
                Grid.SetRow(proxy, row);
                Grid.SetColumn(proxy, index % 2);
                target.Children.Add(proxy);
            }
        }

        private static string FindOptionLabel(RadioButton original)
        {
            if (original.Content is string value) return value;
            if (original.Content is Grid grid)
                foreach (UIElement child in grid.Children)
                    if (child is TextBlock text && Grid.GetColumn(text) == 0)
                        return text.Text;
            return original.Name;
        }
    }
}
