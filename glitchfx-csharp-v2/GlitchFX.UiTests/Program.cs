using System;
using System.IO;
using System.Threading;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
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
    ///   dotnet run --project . -- "..\GlitchFX\bin\Release\net8.0-windows\GlitchFX.exe"
    ///
    /// The CI workflow (.github/workflows/glitchfx-csharp-v2.yml) runs this
    /// automatically on windows-latest and uploads the screenshots folder as
    /// a build artifact named "glitchfx-ui-screenshots".
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

            string screenshotDir = Path.Combine(AppContext.BaseDirectory, "screenshots");
            Directory.CreateDirectory(screenshotDir);

            using var app = Application.Launch(exePath);
            using var automation = new UIA3Automation();
            Window? window = null;
            try
            {
                window = app.GetMainWindow(automation, TimeSpan.FromSeconds(15));
                if (window == null)
                {
                    Console.Error.WriteLine("Main window did not appear within 15s.");
                    return 1;
                }
                Thread.Sleep(1000); // let initial layout/render settle
                Screenshot(window, screenshotDir, "01_initial_effects_tab");

                ClickByName(window, "Output", screenshotDir, "02_output_tab");
                ClickByName(window, "Effects", screenshotDir, "03_back_to_effects_tab");
                ClickByName(window, "Randomize", screenshotDir, "04_after_randomize");

                Console.WriteLine($"Done. Screenshots saved to: {screenshotDir}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Smoke test failed: {ex}");
                if (window != null) Screenshot(window, screenshotDir, "ERROR_state");
                return 1;
            }
            finally
            {
                try { app.Close(); } catch { /* already closed */ }
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
