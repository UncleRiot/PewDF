using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace PewDF.Features
{
    /// <summary>
    /// About card metadata, signer information, and executable hash.
    ///
    /// Holds no controls. Talks to the window only through <see cref="IAboutHost"/>, so the whole
    /// of this file is testable against a stub host.
    /// </summary>
    internal sealed class AboutController
    {
        // The certificate subject is the legal name ("Open Source Developer Stephen Riley"), so the
        // About card ties it back to the name people know. Gated on the subject actually being
        // Steve's: a fork signed by somebody else must not claim the alias, and an unsigned build
        // has no subject at all. Family standard, see code/CLAUDE.md.
        private const string SignerName = "Stephen Riley";
        private const string AkaName    = "Steve the Killer";

        private const string Repo = "https://github.com/UncleRiot/KPDF";

        private readonly IAboutHost _host;

        internal AboutController(IAboutHost host) => _host = host;

        /// <summary>The running build's SemVer, including a prerelease label when present.</summary>
        internal static string Version => AppVersion.Display;

        /// <summary>Release date baked in from the csproj's ReleaseDate property, so a user can see
        /// how old their build is. A file timestamp would not survive being copied and the PE linker
        /// stamp is a build date, not a release date. Empty when the attribute is missing (an older
        /// build), in which case the version line shows the version alone.</summary>
        internal static string ReleaseDate
        {
            get
            {
                foreach (var a in System.Reflection.CustomAttributeExtensions.GetCustomAttributes
                             <System.Reflection.AssemblyMetadataAttribute>(
                                 System.Reflection.Assembly.GetExecutingAssembly()))
                    if (a.Key == "ReleaseDate") return a.Value ?? string.Empty;
                return string.Empty;
            }
        }

        /// <summary>Populates the card and shows it. The SHA-256 is slow, so it lands later.</summary>
        internal void Show()
        {
            var (sigValid, sigSubject, sigThumbprint) = App.GetExeSignerInfo();

            _host.Publisher   = "Daniel Capilla";
            _host.Thumbprint  = string.IsNullOrEmpty(sigThumbprint) ? _host.Loc("Str_Margin_None") : sigThumbprint;
            _host.Sha256      = _host.Loc("Str_About_Computing");
            _host.ReleaseDate = ReleaseDate;

            _host.SetVersion(Version);

            // Signed, verified, AND signed by Steve - all three, not merely "is signed".
            bool signedByMe = sigValid
                           && sigSubject.Contains(SignerName, StringComparison.OrdinalIgnoreCase);
            // 0x201C / 0x201D are the curly quotes, built from codepoints so this file stays ASCII
            // on disk - the same encoding trap that made release.ps1 PS7-only.
            _host.SetAlias(signedByMe ? (char)0x201C + AkaName + (char)0x201D : null);

            _host.ShowCard();

            ComputeSha256Async();
        }

        /// <summary>Opens the GitHub release for the running version.</summary>
        internal static void OpenReleaseNotes() => OpenUrl($"{Repo}/releases/tag/v{Version}");

        internal static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* no browser, or the shell refused - nothing useful to say */ }
        }

        // ---- SHA-256 -------------------------------------------------------------------------

        private async void ComputeSha256Async()
        {
            var sha256 = await System.Threading.Tasks.Task.Run(App.GetExeSha256).ConfigureAwait(true);
            _host.Sha256 = sha256;
        }

        // SEMANTIC ANCHOR: ABOUT_METADATA_ONLY_INVARIANT: About reads local build metadata and opens explicit links; it never checks, downloads, or installs releases.
    }
}
