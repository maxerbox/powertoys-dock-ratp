using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace RatpDock;

/// <summary>
/// Opens the itinerary URL in the browser <em>and brings the browser window to
/// the foreground</em>. The palette's built-in OpenUrlCommand launches via
/// LaunchUriAsync, which opens a background tab in an already-running browser
/// without focusing it; ShellExecute + SetForegroundWindow gives the expected
/// "click → see my itinerary" behavior.
/// </summary>
internal sealed partial class OpenItineraryCommand : InvokableCommand
{
    private readonly string _url;

    public OpenItineraryCommand(string url, string title = "Open itinerary")
    {
        _url = url;
        Name = title;
        Icon = new IconInfo("\uE8A7"); // Map
    }

    public override CommandResult Invoke()
    {
        try
        {
            // ShellExecute activates the browser (unlike LaunchUriAsync which
            // opens background tabs in an already-running browser).
            using var process = Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true });
            if (process is not null)
            {
                _ = FocusAsync(process);
            }
        }
        catch (Exception ex)
        {
            // Never block the palette on a launch failure — surface it in the name.
            Name = $"Could not open: {ex.Message}";
        }

        return CommandResult.Dismiss();
    }

    /// <summary>
    /// Best-effort foregrounding: waits for the browser window to exist, then
    /// activates it. Covers browsers that still open the tab in the background.
    /// </summary>
    private static async Task FocusAsync(Process launched)
    {
        try
        {
            // The launched process may just be a shim that forwards to the
            // already-running browser and exits — walk to the real browser.
            var browser = await WaitForWindowAsync(launched, TimeSpan.FromSeconds(2)).ConfigureAwait(false)
                ?? FindBrowserWithWindow();

            if (browser is null || browser.HasExited)
            {
                return;
            }

            if (browser.MainWindowHandle != 0)
            {
                ForceForeground(browser.MainWindowHandle);
            }
        }
        catch
        {
            // Focusing is best-effort; ignore any failure.
        }
    }

    private static async Task<Process?> WaitForWindowAsync(Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(100).ConfigureAwait(false);
            try
            {
                process.Refresh();
                if (!process.HasExited && process.MainWindowHandle != 0)
                {
                    return process;
                }

                if (process.HasExited)
                {
                    return null; // shim forwarded to an existing browser process
                }
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Finds a running browser that owns a visible top-level window.</summary>
    private static Process? FindBrowserWithWindow() =>
        Process.GetProcessesByName("chrome").Concat(Process.GetProcessesByName("msedge"))
            .Concat(Process.GetProcessesByName("firefox")).Concat(Process.GetProcessesByName("brave"))
            .FirstOrDefault(p =>
            {
                try
                {
                    return !p.HasExited && p.MainWindowHandle != 0;
                }
                catch
                {
                    return false;
                }
            });

    private static void ForceForeground(IntPtr handle)
    {
        // A window that is minimized must be restored before it can be focused.
        _ = ShowWindow(handle, IsIconic(handle) ? SW_RESTORE : SW_SHOW);
        _ = SetForegroundWindow(handle);
    }

    private const uint SW_RESTORE = 9;
    private const uint SW_SHOW = 5;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, uint nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
}
