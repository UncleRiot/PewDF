using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace PewDF.Services;

// SEMANTIC ANCHOR: PDF_DEFAULT_ASSOCIATION_SSOT
// Registration only advertises an app capability. UserChoice remains Windows-owned.
// Portable binaries are registered at their actual current location, never at guessed install paths.
internal static class PdfDefaultApplication
{
    private const string ProgId = "PewDF.PDF";
    internal const string SuppressPromptSetting = "SuppressDefaultPdfPrompt";

    internal static bool IsDefault
    {
        get
        {
            try
            {
                using var choice = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice");
                return string.Equals(choice?.GetValue("ProgId") as string, ProgId, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    internal static void RegisterAndOpenWindowsSettings()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
        using (var app = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId)
            ?? throw new InvalidOperationException("Cannot register PDF application."))
        {
            app.SetValue("", "PewDF PDF Document");
            app.CreateSubKey("DefaultIcon")?.SetValue("", $"\"{exe}\",0");
            app.CreateSubKey(@"shell\open\command")?.SetValue("", $"\"{exe}\" \"%1\"");
        }
        using (var capabilities = Registry.CurrentUser.CreateSubKey(@"Software\PewDF\Capabilities")
            ?? throw new InvalidOperationException("Cannot register PDF capabilities."))
        {
            capabilities.SetValue("ApplicationName", "PewDF");
            capabilities.SetValue("ApplicationDescription", "PewDF PDF viewer");
            capabilities.CreateSubKey("FileAssociations")?.SetValue(".pdf", ProgId);
        }
        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications")
            ?? throw new InvalidOperationException("Cannot advertise PDF capabilities."))
            registered.SetValue("PewDF", @"Software\PewDF\Capabilities");
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
    }
}
