using System;
using System.IO;
using System.Threading;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.UIA3;

namespace GlitchFX.UiTests
{
    /// <summary>
    /// Automated smoke-test / screenshot script for the Glitch FX WPF app.
    ///
    /// Launches the built GlitchFX.exe, walks through the main UI surfaces
    /// (Effects tab, Output tab, Randomize) and saves a screenshot after each
    /// step to ./screenshots, so UI regressions can be spotted from images
    /// alone instead of someone manually opening the app every time.
    ///
    /// This intentionally is NOT a unit test framework (no xunit/nunit) - it's
    /// a small standalone console script, matching the "script that tests the
    /// software and takes screenshots" ask directly: run it, look at the PNGs.
    ///
    /// Usage (from a Windows machine with a real desktop session -
    /// FlaUI/UIA drives real window handles and does not work on a truly
    /// headless box):
    ///   dotnet build ..\GlitchFX\GlitchFX.csproj -c Release
    ///   dotnet run --project . -- "..\GlitchFX\bin\Release\net8.0-windows\GlitchFX.exe" ["..\path\to\test_video.mp4"]
    ///
    /// The optional second argument is a video file path. When provided, it
    /// is forwarded to GlitchFX.exe as a command-line argument, which the app
    /// auto-loads on startup (see MainWindow's constructor), so this script
    /// can exercise the actual preview-rendering pipeline instead of just the
    /// empty "Drop a video here" placeholder state.
    ///
    /// The CI workflow (.github/workflows/glitchfx-csharp-v2.yml) runs this
    /// automatically on windows-latest and uploads the screenshots folder as
    /// a build artifact named "glitchfx-ui-screenshots".
    ///
    /// Diagnostics guarantee: previously, if the app's main window never
    /// appeared (crashed on startup, or the runner restricts interactive UI
    /// automation for launched GUI apps) or Application.Launch itself threw,
    /// NOTHING was ever written to the screenshots folder, so the CI upload
    /// step legitimately found zero files ("No files were found...") even
    /// though the run itself produced no useful diagnostic signal either. Both
    /// failure paths below now call SaveDiagnostics(), which always writes a
    /// .txt explaining what happened plus (best-effort) a full-screen capture,
    /// so the artifact is never empty and actually explains the failure.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            string exePath = args.Length > 0 ? args[0] : FindDefaultExePath();
            if (!File.Exists(exePath))
            {
                Console.Error.WriteLine($"Could not find GlitchFX.exe at '{exePath}'. Build the app first (dotnet build -c Release) or pass the .exe path as an argument.");
                return 1;
            }

            string? videoPath = args.Length > 1 && File.Exists(args[1]) ? Path.GetFullPath(args[1]) : null;

            string screenshotDir = Path.Combine(AppContext.BaseDirectory, "screenshots");
            Directory.CreateDirectory(screenshotDir);

            Application? app = null;
            Window? window = null;
            try
            {
                app = videoPath != null
                    ? Application.Launch(exePath, $"\"{videoPath}\"")
                    : Application.Launch(exePath);
                using var automation = new UIA3Automation();

                window = app.GetMainWindow(automation, TimeSpan.FromSeconds(15));
                if (window == null)
                {
                    Console.Error.WriteLine("Main window did not appear within 15s.");
                    SaveDiagnostics(screenshotDir, "MISSING_main_window",
                        "GetMainWindow returned null within the 15s timeout.\n\n" +
                        "Most likely causes:\n" +
                        "  1. GlitchFX.exe threw an unhandled exception on startup (check whether " +
                        "the process is still running / its exit code, and whether recent XAML or " +
                        "code-behind changes reference each other correctly).\n" +
                        "  2. This runner restricts interactive UI automation for launched GUI apps " +
                        "(some windows-latest configurations do this even though the app itself is fine).\n\n" +
                        "The accompanying .png (if present) is a full-screen capture, not a window " +
                        "capture, since no window handle was ever found.");
                    return 1;
                }
                Thread.Sleep(1000); // let initial layout/render settle
                Screenshot(window, screenshotDir, "01_initial_effects_tab");

                if (videoPath != null)
                {
                    // Give the video reader thread time to open the file and
                    // push the first decoded/processed preview frame through
                    // the effect pipeline.
                    Thread.Sleep(1500);
                    Screenshot(window, screenshotDir, "02_video_loaded");
                }

                ClickByName(window, "Output", screenshotDir, "03_output_tab");
                ClickByName(window, "Effects", screenshotDir, "04_back_to_effects_tab");

                ScrollEffectsListIntoView(window);
                Screenshot(window, screenshotDir, "05_effects_cards_scrolled");

                ClickByName(window, "Randomize", screenshotDir, "06_after_randomize");

                Console.WriteLine($"Done. Screenshots saved to: {screenshotDir}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Smoke test failed: {ex}");
                if (window != null) Screenshot(window, screenshotDir, "ERROR_state");
                else SaveDiagnostics(screenshotDir, "ERROR_before_window", $"Unhandled exception before any window was found:\n\n{ex}");
                return 1;
            }
            finally
            {
                try { app?.Close(); } catch { /* already closed, or never launched */ }
                try { app?.Dispose(); } catch { /* best effort */ }
            }
        }

        private static void ClickByName(Window window, string buttonName, string dir, string label)
        {
            var button = window.FindFirstDescendant(cf => cf.ByName(buttonName))?.AsButton();
            if (button == null)
            {
                Console.Error.WriteLine($"Could not find button '{buttonName}'.");
                Screenshot(window, dir, $"MISSING_{label}");
                return;
            }
            button.Invoke();
            Thread.Sleep(500);
            Screenshot(window, dir, label);
        }

        /// <summary>
        /// The Effects tab's per-effect cards (color_grade, posterize, etc.)
        /// render below the Beat Sync / Global cards inside a ScrollViewer,
        /// off the bottom of the initial viewport.
        ///
        /// A synthetic OS-level mouse wheel (FlaUI's Mouse.Scroll) turned out
        /// to be unreliable against this WPF ScrollViewer in CI - screenshots
        /// before/after "scrolling" came back identical. WPF's ScrollViewer
        /// automation peer implements UIA's IScrollProvider, so drive that
        /// directly instead: it is a first-class UI Automation pattern, not a
        /// simulated input event, so it reliably moves the viewport even when
        /// synthetic wheel messages get lost. EffectsPanel.xaml exposes the
        /// ScrollViewer via AutomationProperties.AutomationId="EffectsScrollViewer"
        /// specifically so this can find it.
        /// </summary>
        private static void ScrollEffectsListIntoView(Window window)
        {
            const double NoScroll = -1.0; // UIA convention: leave that axis untouched
            try
            {
                var scrollViewerElement = window.FindFirstDescendant(cf => cf.ByAutomationId("EffectsScrollViewer"));
                var scrollPattern = scrollViewerElement?.Patterns.Scroll.PatternOrDefault;
                if (scrollPattern != null)
                {
                    scrollPattern.SetScrollPercent(NoScroll, 100.0);
                    Thread.Sleep(300);
                    return;
                }
                Console.Error.WriteLine("EffectsScrollViewer has no Scroll pattern available; falling back to mouse wheel.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Scroll pattern approach failed ({ex.Message}); falling back to mouse wheel.");
            }

            // Fallback for older builds / if UIA doesn't expose the pattern.
            try
            {
                var bounds = window.BoundingRectangle;
                var scrollPoint = new System.Drawing.Point((int)(bounds.Left + 170), (int)(bounds.Top + 400));
                Mouse.MoveTo(scrollPoint);
                for (int i = 0; i < 30; i++) Mouse.Scroll(-4);
                Thread.Sleep(300);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not scroll effects list: {ex.Message}");
            }
        }

        private static void Screenshot(Window window, string dir, string label)
        {
            try
            {
                var image = Capture.Element(window);
                image.ToFile(Path.Combine(dir, $"{label}.png"));
                Console.WriteLine($"Saved screenshot: {label}.png");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to capture screenshot '{label}': {ex.Message}");
            }
        }

        /// <summary>
        /// Last-resort diagnostics for the two paths where no window was ever
        /// available to screenshot: writes a .txt explaining what happened,
        /// plus a best-effort full-screen (not window-scoped) capture. Always
        /// leaves at least the .txt behind, so the CI artifact upload never
        /// comes back with "No files were found" with zero explanation.
        /// </summary>
        private static void SaveDiagnostics(string dir, string label, string message)
        {
            try
            {
                File.WriteAllText(Path.Combine(dir, $"{label}.txt"), message);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to write diagnostic log '{label}.txt': {ex.Message}");
            }

            try
            {
                var image = Capture.Screen();
                image.ToFile(Path.Combine(dir, $"{label}.png"));
                Console.WriteLine($"Saved full-screen fallback screenshot: {label}.png");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to capture full-screen fallback screenshot '{label}': {ex.Message}");
            }
        }

        private static string FindDefaultExePath()
        {
            string[] candidates =
            {
                Path.Combine("..", "GlitchFX", "bin", "Release", "net8.0-windows", "GlitchFX.exe"),
                Path.Combine("..", "GlitchFX", "bin", "Debug", "net8.0-windows", "GlitchFX.exe"),
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
            return candidates[0];
        }
    }
}
