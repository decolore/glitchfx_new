using System;
using System.IO;
using System.Linq;
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
    /// Launches the built GlitchFX.exe and walks through a broad slice of the
    /// real UI: the Effects/Output tabs, Randomize, expanding a per-effect
    /// card, the global Strength slider, Play/Pause, a real mouse-driven
    /// timeline seek, Undo/Redo, an Output-tab setting (CRF), and the
    /// export-with-no-output-path warning dialog - saving a screenshot after
    /// each step to ./screenshots, so UI regressions can be spotted from
    /// images alone instead of someone manually opening the app every time.
    ///
    /// Each step after the initial launch runs through RunStep(), which
    /// isolates it in its own try/catch: if one step throws (e.g. because
    /// GlitchFX.exe itself crashed partway through the run, or a control was
    /// renamed), that failure is recorded - a "{label}.txt" with the full
    /// exception, a best-effort screenshot, and a crash.log copy if one
    /// exists - and the script moves on to the next step instead of aborting
    /// the whole run.
    ///
    /// Toolbar buttons are looked up by AutomationId (ClickByAutomationId),
    /// not by their visible text (ClickByName is kept only for the plain-
    /// string-content Effects/Output tab buttons). Any button whose Content
    /// is a composite StackPanel (an icon plus a TextBlock label - which is
    /// most of this app's toolbar buttons) gets no derived accessible Name of
    /// its own, so a ByName search matches its inner TextBlock instead of the
    /// Button; FlaUI's .AsButton() wraps that TextBlock without complaint,
    /// but calling .Invoke() on it then throws "Native pattern is null"
    /// because a TextBlock's automation peer has no Invoke provider. Every
    /// such button (RandomizeButton, UndoButton, RedoButton, ExportButton,
    /// etc.) now has an explicit AutomationProperties.AutomationId in XAML
    /// specifically so this script can target the real Button reliably.
    ///
    /// This intentionally is NOT a unit test framework (no xunit/nunit) - it's
    /// a small standalone console script, matching the "script that tests the
    /// software and takes screenshots" ask directly: run it, look at the PNGs
    /// (and any *.txt/*_crash.log files if something went wrong).
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
    /// Note on the timeline seek step: it deliberately drives a real OS mouse
    /// click on the slider (via FlaUI's Mouse.Click) instead of setting its
    /// Value through the UIA RangeValue pattern, because MainWindow's seek
    /// logic only actually calls Bridge.Seek() while _draggingTimeline is
    /// true (set from the slider's PreviewMouseLeftButtonDown/Up handlers) -
    /// a programmatic RangeValue.SetValue() would move the thumb visually but
    /// silently skip the real seek, which would make this step a fake test.
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

            // Clear out any crash.log left over from a previous run in this same
            // output folder, so a stale log is never mistaken for this run's.
            string exeDir = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? AppContext.BaseDirectory;
            string crashLogPath = Path.Combine(exeDir, "crash.log");
            try { File.Delete(crashLogPath); } catch { /* ignore */ }

            Application? app = null;
            Window? window = null;

            // Isolates one test step: if `action` throws (most commonly
            // because GlitchFX.exe crashed partway through the run, making
            // every subsequent FlaUI call against `window` throw), this logs
            // it, always writes "{label}.txt" with the full exception text,
            // attempts a best-effort screenshot, and copies crash.log if one
            // now exists - then returns so the *next* step still gets a
            // chance to run, instead of one bad step silently ending the
            // whole script.
            void RunStep(string label, Action action)
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Step '{label}' failed: {ex}");
                    SaveDiagnostics(screenshotDir, $"ERROR_{label}", crashLogPath, window,
                        $"Step '{label}' threw an unhandled exception:\n\n{ex}");
                }
            }

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
                    SaveDiagnostics(screenshotDir, "MISSING_main_window", crashLogPath, null,
                        "GetMainWindow returned null within the 15s timeout.\n\n" +
                        "Most likely causes:\n" +
                        "  1. GlitchFX.exe threw an unhandled exception on startup - check the " +
                        "accompanying *_crash.log file (copied from next to GlitchFX.exe) for details.\n" +
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

                RunStep("03_output_tab", () => ClickByAutomationId(window!, "OutputTabButton", screenshotDir, "03_output_tab"));
                RunStep("04_back_to_effects_tab", () => ClickByAutomationId(window!, "EffectsTabButton", screenshotDir, "04_back_to_effects_tab"));
                RunStep("05_effects_cards_scrolled", () =>
                {
                    ScrollEffectsListIntoView(window!);
                    Screenshot(window!, screenshotDir, "05_effects_cards_scrolled");
                });
                RunStep("06_after_randomize", () => ClickByAutomationId(window!, "RandomizeButton", screenshotDir, "06_after_randomize"));

                // Every effect card starts collapsed after Randomize rebinds
                // the project; expand the first one to exercise the
                // per-parameter rows (sliders/combos/checkboxes) rendering.
                RunStep("07_first_effect_card_expanded", () => ClickByAutomationId(window!, "EffectCollapseButton_0", screenshotDir, "07_first_effect_card_expanded"));

                // Global Strength: push it well past 100% (true strength, not
                // clamped to each effect's own Max - by design) and confirm
                // the toolbar reflects the change.
                RunStep("08_strength_adjusted", () => SetSliderValue(window!, "StrengthSlider", 3.5, screenshotDir, "08_strength_adjusted"));

                RunStep("09_playing", () =>
                {
                    ClickByAutomationId(window!, "PlayPauseButton", screenshotDir, "09_playing");
                    Thread.Sleep(800); // let a bit of real playback advance before seeking
                });

                RunStep("10_timeline_seek", () => SeekTimeline(window!, 0.6, screenshotDir, "10_timeline_seek"));
                RunStep("11_paused", () => ClickByAutomationId(window!, "PlayPauseButton", screenshotDir, "11_paused"));
                RunStep("12_after_undo", () => ClickByAutomationId(window!, "UndoButton", screenshotDir, "12_after_undo"));
                RunStep("13_after_redo", () => ClickByAutomationId(window!, "RedoButton", screenshotDir, "13_after_redo"));
                RunStep("14_output_tab_revisited", () => ClickByAutomationId(window!, "OutputTabButton", screenshotDir, "14_output_tab_revisited"));
                RunStep("15_crf_adjusted", () => SetSliderValue(window!, "CrfSlider", 28, screenshotDir, "15_crf_adjusted"));
                RunStep("16_17_export_warning", () => TestExportMissingPathWarning(window!, screenshotDir));

                Console.WriteLine($"Done. Screenshots saved to: {screenshotDir}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Smoke test failed: {ex}");
                SaveDiagnostics(screenshotDir, window != null ? "ERROR_state" : "ERROR_before_window", crashLogPath, window,
                    $"Unhandled exception outside of an isolated step (during launch/window discovery):\n\n{ex}");
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
        /// Same as ClickByName, but looks up the element by its UI Automation
        /// AutomationId instead of its accessible Name. Preferred for every
        /// toolbar button (see the class doc comment for why ByName is
        /// unreliable for buttons with composite icon+label content), and
        /// required for icon-only buttons (Play/Pause) or controls built
        /// dynamically in code (per-effect-card collapse buttons).
        /// </summary>
        private static void ClickByAutomationId(Window window, string automationId, string dir, string label)
        {
            var button = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId))?.AsButton();
            if (button == null)
            {
                Console.Error.WriteLine($"Could not find button with AutomationId '{automationId}'.");
                Screenshot(window, dir, $"MISSING_{label}");
                return;
            }
            button.Invoke();
            Thread.Sleep(500);
            Screenshot(window, dir, label);
        }

        /// <summary>
        /// Sets a Slider's value via UI Automation's RangeValue pattern (a
        /// first-class automation pattern, not a simulated input event) and
        /// screenshots the result. Safe for sliders whose ValueChanged
        /// handler reacts unconditionally (StrengthSlider, CrfSlider) - NOT
        /// for TimelineSlider, which requires a real mouse-driven interaction;
        /// see SeekTimeline.
        /// </summary>
        private static void SetSliderValue(Window window, string automationId, double value, string dir, string label)
        {
            var element = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            var rangePattern = element?.Patterns.RangeValue.PatternOrDefault;
            if (rangePattern == null)
            {
                Console.Error.WriteLine($"Could not find a RangeValue pattern for '{automationId}'.");
                Screenshot(window, dir, $"MISSING_{label}");
                return;
            }
            rangePattern.SetValue(value);
            Thread.Sleep(400);
            Screenshot(window, dir, label);
        }

        /// <summary>
        /// Seeks the bottom timeline bar with a real OS-level mouse click at
        /// the given horizontal fraction (0-1) of the slider's bounds. See the
        /// class doc comment for why this must be a real click rather than a
        /// RangeValue.SetValue() call.
        /// </summary>
        private static void SeekTimeline(Window window, double fraction, string dir, string label)
        {
            var slider = window.FindFirstDescendant(cf => cf.ByAutomationId("TimelineSlider"));
            if (slider == null)
            {
                Console.Error.WriteLine("Could not find TimelineSlider.");
                Screenshot(window, dir, $"MISSING_{label}");
                return;
            }
            var bounds = slider.BoundingRectangle;
            var point = new System.Drawing.Point(
                (int)(bounds.Left + bounds.Width * fraction),
                (int)(bounds.Top + bounds.Height / 2));
            Mouse.Click(point);
            Thread.Sleep(600);
            Screenshot(window, dir, label);
        }

        /// <summary>
        /// Clicking Export with no output path set should show the app's
        /// dark warning dialog (AppDialog) instead of silently doing nothing
        /// or crashing - this exercises the export validation + custom dialog
        /// UI without needing to run a real (slower, codec-dependent) ffmpeg
        /// export. AppDialog is a genuine modal child window of the main
        /// window, so it shows up in Window.ModalWindows once ExportButton's
        /// click has triggered AppDialog.Show(...).
        /// </summary>
        private static void TestExportMissingPathWarning(Window window, string dir)
        {
            var exportButton = window.FindFirstDescendant(cf => cf.ByAutomationId("ExportButton"))?.AsButton();
            if (exportButton == null)
            {
                Console.Error.WriteLine("Could not find the Export Video button (AutomationId 'ExportButton').");
                Screenshot(window, dir, "MISSING_16_export_button");
                return;
            }
            exportButton.Invoke();
            Thread.Sleep(600);

            var dialogWindow = window.ModalWindows.FirstOrDefault();
            if (dialogWindow == null)
            {
                Console.Error.WriteLine("Expected a warning dialog after exporting with no output path, but no modal window was found.");
                Screenshot(window, dir, "MISSING_16_export_warning_dialog");
                return;
            }

            try
            {
                var image = Capture.Element(dialogWindow);
                image.ToFile(Path.Combine(dir, "16_export_missing_path_warning.png"));
                Console.WriteLine("Saved screenshot: 16_export_missing_path_warning.png");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to capture the warning dialog: {ex.Message}");
            }

            var okButton = dialogWindow.FindFirstDescendant(cf => cf.ByName("OK"))?.AsButton();
            if (okButton == null)
            {
                Console.Error.WriteLine("Could not find the warning dialog's OK button.");
                return;
            }
            okButton.Invoke();
            Thread.Sleep(300);
            Screenshot(window, dir, "17_after_dismissing_warning");
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
        /// Last-resort diagnostics for any failure path - a step throwing via
        /// RunStep, the main window never appearing, or an exception outside
        /// any isolated step. Always writes a "{label}.txt" with `message`
        /// (this alone guarantees a non-empty, explanatory artifact even when
        /// every other capture attempt below fails), then best-effort:
        /// a screenshot (window-scoped via `window` if it's still alive, else
        /// a full-screen capture) and a copy of GlitchFX.exe's own crash.log
        /// (written by App.xaml.cs's global unhandled-exception handlers) if
        /// one exists at `crashLogPath`.
        /// </summary>
        private static void SaveDiagnostics(string dir, string label, string crashLogPath, Window? window, string message)
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
                var image = window != null ? Capture.Element(window) : Capture.Screen();
                image.ToFile(Path.Combine(dir, $"{label}.png"));
                Console.WriteLine($"Saved {(window != null ? "window" : "full-screen fallback")} screenshot: {label}.png");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to capture screenshot '{label}': {ex.Message}. This usually means GlitchFX.exe's window is no longer available (e.g. it crashed) - check the accompanying crash.log copy, if any.");
            }

            try
            {
                if (File.Exists(crashLogPath))
                {
                    File.Copy(crashLogPath, Path.Combine(dir, $"{label}_crash.log"), overwrite: true);
                    Console.WriteLine($"Copied GlitchFX.exe's crash.log as {label}_crash.log");
                }
                else
                {
                    Console.WriteLine($"No crash.log was found next to GlitchFX.exe for '{label}' (it may not have crashed, or exited cleanly).");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to copy crash.log for '{label}': {ex.Message}");
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
