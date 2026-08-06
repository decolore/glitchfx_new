using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace GlitchFX
{
    /// <summary>
    /// GlitchFX is a WinExe (no console attached), so historically an
    /// unhandled exception on any thread just silently terminated the
    /// process with zero diagnostic trace anywhere. That made the CI UI
    /// smoke test's "Could not find process with id: N" / "Main window did
    /// not appear within 15s" failures (see GlitchFX.UiTests/Program.cs)
    /// impossible to root-cause: the app died before its main window ever
    /// appeared, and nothing was written down anywhere to explain why.
    ///
    /// These handlers cover every unhandled-exception surface WPF/.NET
    /// exposes (UI-thread dispatcher, any other thread, unobserved async
    /// task faults) and always write full exception details - including
    /// inner exceptions and stack traces - to crash.log next to GlitchFX.exe
    /// before the process exits, so the actual root cause is always
    /// recoverable afterward instead of a silent, undiagnosable crash.
    /// </summary>
    public partial class App : Application
    {
        public App()
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                LogCrash("UnobservedTaskException", e.Exception);
                e.SetObserved();
            };
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogCrash("DispatcherUnhandledException (UI thread)", e.Exception);
            // Mark handled so WPF doesn't also try to show its own crash UI,
            // then exit deliberately and immediately - the crash log is
            // already flushed to disk by this point, and there's no good
            // reason to keep a broken UI thread limping along.
            e.Handled = true;
            Environment.Exit(1);
        }

        private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            LogCrash("AppDomain.UnhandledException (non-UI thread)", e.ExceptionObject as Exception);
        }

        private static readonly object LogLock = new();

        private static void LogCrash(string source, Exception? ex)
        {
            try
            {
                lock (LogLock)
                {
                    string path = Path.Combine(AppContext.BaseDirectory, "crash.log");
                    File.AppendAllText(path, $"[{DateTime.UtcNow:O}] {source}\n{ex}\n\n");
                }
            }
            catch
            {
                // Best effort only - if we can't even write the crash log,
                // there's nothing else productive to do here.
            }
        }
    }
}
