using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace FileNinja.FlowLauncher;

/// <summary>
/// Append-only diagnostic log at %TEMP%\FileNinja.FlowLauncher.log. The whole
/// point is to capture life-cycle signals when Flow.Launcher misbehaves —
/// either the plugin never loads (no entries appear), loads but never gets
/// queried (only Init entries), or queries hang somewhere downstream (entries
/// stop mid-flow).
///
/// Self-isolated: never throws, never references the SDK, swallows all I/O
/// errors. Safe to call from anywhere including type initializers.
/// </summary>
internal static class PluginLog
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "FileNinja.FlowLauncher.log");
    private static readonly object Gate = new();

    /// <summary>Static ctor fires on assembly load — the very first signal we have.</summary>
    static PluginLog()
    {
        try
        {
            Write("==== assembly loaded "
                + $"pid={Process.GetCurrentProcess().Id} "
                + $"clr={Environment.Version} "
                + $"tfm={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} "
                + $"path={typeof(PluginLog).Assembly.Location}");
        }
        catch { /* never throw from a type initializer */ }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] [t{Thread.CurrentThread.ManagedThreadId,2}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Fall back to debug output if disk is somehow unavailable. We do not
            // want logging itself to be the cause of a hang or crash.
            try { Debug.WriteLine("[FileNinja.Flow] " + message); } catch { }
        }
    }

    public static void WriteException(string where, Exception ex)
    {
        Write($"EXCEPTION at {where}: {ex.GetType().Name}: {ex.Message}");
        Write(ex.ToString());
    }
}
