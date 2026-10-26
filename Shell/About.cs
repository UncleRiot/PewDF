using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PewDF.Features;

namespace PewDF
{
    /// <summary>
    /// The About overlay's window half: the IAboutHost implementation that maps the controller's
    /// values onto the named XAML elements, the inline construction the card needs, and the click
    /// handlers. The signature and hash logic lives in
    /// <see cref="AboutController"/>.
    ///
    /// NOTE: this stays "namespace PewDF" rather than PewDF.Shell, because it is a partial
    /// of MainWindow and every partial of a class must share one namespace. It moves to
    /// PewDF.Shell when MainWindow itself does.
    /// </summary>
    public partial class MainWindow : IAboutHost
    {
        private AboutController? _aboutController;
        private AboutController About => _aboutController ??= new AboutController(this);

        private void ShowAboutOverlay() => About.Show();

        private void CloseAboutOverlay()
        {
            FadeOverlayOut(AboutOverlay);
        }

        // ---- IShellServices ------------------------------------------------------------------

        Window IShellServices.Window => this;

        // MainWindow's own Loc and SetStatus are private, and a private member cannot implement an
        // interface. Forwarding explicitly satisfies IShellServices without widening either of them
        // to public, which would be a change nobody asked for.
        string IShellServices.Loc(string key) => Loc(key);
        void IShellServices.SetStatus(string text) => SetStatus(text);

        // ---- IAboutHost ----------------------------------------------------------------------

        string IAboutHost.Publisher   { set => AboutPublisherBlock.Text  = value; }
        string IAboutHost.Thumbprint  { set => AboutThumbprintBlock.Text = value; }
        string IAboutHost.Sha256      { set => AboutSha256Block.Text     = value; }
        string IAboutHost.ReleaseDate { set => AboutReleaseDateBlock.Text = value; }
        /// <summary>Version line, as a hyperlink through to that release tag.</summary>
        void IAboutHost.SetVersion(string version)
        {
            AboutVersionBlock.Inlines.Clear();
            AboutVersionBlock.Inlines.Add(AccentLink($"v{version}", () => AboutController.OpenReleaseNotes()));
        }

        /// <summary>The AKA line. Null hides it entirely.</summary>
        void IAboutHost.SetAlias(string? alias)
        {
            AboutAkaBlock.Visibility = alias is null ? Visibility.Collapsed : Visibility.Visible;
            if (alias is null) return;

            AboutAkaBlock.Inlines.Clear();
            AboutAkaBlock.Inlines.Add(new Run(Loc("Str_About_Aka") + " ") { Foreground = Res("MutedTextBrush") });
            var hl = AccentLink(alias, () => AboutController.OpenUrl("https://thekiller.net"));
            hl.ToolTip = "thekiller.net";
            AboutAkaBlock.Inlines.Add(hl);
        }

        /// <summary>Dismisses any other full-window overlay, then fades the card in. The overlays
        /// are mutually exclusive rather than stacking on top of one another.</summary>
        void IAboutHost.ShowCard()
        {
            if (ShortcutOverlay.Visibility == Visibility.Visible) FadeOverlayOut(ShortcutOverlay);

            BuildAboutStaticContent();
            FadeOverlayIn(AboutOverlay);
        }

        // ---- Card content independent of signature state -------------------------------------

        private void BuildAboutStaticContent()
        {
            // Reuse the main window's film-grain texture on the About card.
            if (GrainBrush?.ImageSource != null) AboutGrainBrush.ImageSource = GrainBrush.ImageSource;

            BuildAboutWordmark();
            BuildAboutTagline();
            AboutBaseVersionBlock.Text = System.Reflection.Assembly.GetExecutingAssembly()
                .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .OfType<System.Reflection.AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "KillerPdfBaseVersion")?.Value ?? "";
            LoadAboutImages();
        }

        /// <summary>Display the PewDF name without linking to the upstream project.</summary>
        private void BuildAboutWordmark()
        {
            AboutLogoBlock.Inlines.Clear();
            AboutLogoBlock.Inlines.Add(new Run("PewDF") { Foreground = Res("TextBrush") });
        }

        /// <summary>Localized description and attribution of the upstream fork.</summary>
        private void BuildAboutTagline()
        {
            AboutTaglineBlock.Inlines.Clear();
            AboutTaglineBlock.Inlines.Add(new Run(Loc("Str_Tagline")) { Foreground = Res("MutedTextBrush") });
        }

        private void AboutRibbon_Click(object sender, RoutedEventArgs e) => ShowAboutOverlay();

        private void AboutKoFi_Click(object sender, RoutedEventArgs e)
            => AboutController.OpenUrl("https://ko-fi.com/uncleriot");

        private void LoadAboutImages()
        {
            // Optional project resources: retain the built-in icon and readable Ko-fi label
            // until the corresponding files are supplied in Resources.
            LoadOptionalImage("molotov.jpg", AboutMolotovImage, null);
            LoadOptionalImage("ko-fi.png", AboutKoFiImage, AboutKoFiFallback);
        }

        private void LoadOptionalImage(string filename, System.Windows.Controls.Image target,
            System.Windows.Controls.TextBlock? fallback)
        {
            System.Windows.Resources.StreamResourceInfo? resource;
            try
            {
                resource = Application.GetResourceStream(new System.Uri(
                    $"pack://application:,,,/Resources/{filename}", System.UriKind.Absolute));
            }
            catch (System.IO.IOException) { return; }
            if (resource is null) return;
            using (resource.Stream)
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.StreamSource = resource.Stream;
                bitmap.EndInit();
                bitmap.Freeze();
                target.Source = bitmap;
            }
            target.Visibility = Visibility.Visible;
            if (target == AboutMolotovImage) AboutDefaultIcon.Visibility = Visibility.Collapsed;
            if (fallback is not null) fallback.Visibility = Visibility.Collapsed;
        }

        // ---- Small helpers -------------------------------------------------------------------

        private Brush Res(string key) => (Brush)FindResource(key);

        /// <summary>An accent-colored hyperlink with no underline - the family's one treatment for
        /// "this is clickable" on a card.</summary>
        private Hyperlink AccentLink(string text, System.Action onClick)
        {
            var hl = new Hyperlink(new Run(text))
            {
                Foreground      = Res("PrimaryBrush"),
                TextDecorations = null
            };
            hl.Click += (_, _) => onClick();
            return hl;
        }

        // ---- Handlers ------------------------------------------------------------------------

        // Click the dim backdrop to dismiss; a click on the card itself is swallowed.
        private void AboutOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => CloseAboutOverlay();

        private void AboutOverlayCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => e.Handled = true;

        private void AboutOverlayClose_Click(object sender, RoutedEventArgs e)
            => CloseAboutOverlay();

        /// <summary>
        /// "Clear all Data" footer link: wipes settings, downloaded OCR language packs, and temp
        /// files after an explicit confirmation. Destructive, so it always warns first; the user's
        /// PDFs are untouched.
        /// </summary>
        private void AboutClearData_Click(object sender, RoutedEventArgs e)
        {
            var res = PewDialog.Show(this,
                Loc("Str_ClearDataConfirm"),
                Loc("Str_ClearAllData"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;

            App.ClearAllData();
            SetStatus(Loc("Str_St_DataCleared"));
            PewDialog.Show(this,
                Loc("Str_ClearDataDone"),
                Loc("Str_ClearAllData"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
