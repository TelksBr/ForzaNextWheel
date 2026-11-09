using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace ForzaNext.Core.FFB;

public static class FfbDebugLogger
{
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>());
    private static readonly Thread WriterThread;
    private static string? _logPath;
    private static volatile bool _disposed;

    public static bool EnableConsoleOutput { get; set; }

    public static string LogPath
    {
        get
        {
            if (_logPath == null)
            {
                var dir = AppPaths.DataDirectory;
                Directory.CreateDirectory(dir);
                _logPath = Path.Combine(dir, "ffb_debug.log");
            }
            return _logPath;
        }
    }

    static FfbDebugLogger()
    {
        WriterThread = new Thread(ProcessQueue)
        {
            IsBackground = true,
            Name = "FfbDebugLoggerWorker",
            Priority = ThreadPriority.BelowNormal
        };
        WriterThread.Start();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushAndClose();
    }

    public static void Log(string category, string message)
    {
        if (_disposed) return;
        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        string line = $"[{timestamp}] [{category}] {message}";

        if (EnableConsoleOutput)
        {
            // Only output key lifecycle events to console to eliminate scrolling delays
            if (category.StartsWith("DINPUT-CREATE") || category.StartsWith("DINPUT-START") ||
                category.StartsWith("DINPUT-STOP") || category.StartsWith("DINPUT-FREE") ||
                category.StartsWith("HIDHIDE") || category.Contains("ERROR") || category.Contains("WARN"))
            {
                Console.WriteLine(line);
            }
        }

        try
        {
            Queue.TryAdd(line);
        }
        catch
        {
            // Lock-free add, never block caller
        }
    }

    public static void LogError(string category, string message, Exception ex)
    {
        string text = $"{message} - {ex.GetType().Name}: {ex.Message}";
        if (ex is SharpDX.SharpDXException sex)
        {
            text += $" (HRESULT: 0x{sex.HResult:X8}, ResultCode: {sex.ResultCode})";
        }
        Log(category, text);
    }

    private static void ProcessQueue()
    {
        try
        {
            using var file = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 65536);
            using var writer = new StreamWriter(file) { AutoFlush = false };

            int count = 0;
            while (!_disposed || Queue.Count > 0)
            {
                if (Queue.TryTake(out string? line, 100))
                {
                    writer.WriteLine(line);
                    count++;
                    if (count >= 128 || Queue.Count == 0)
                    {
                        writer.Flush();
                        count = 0;
                    }
                }
            }
            writer.Flush();
        }
        catch
        {
            // Never throw from logger background worker
        }
    }

    public static void FlushAndClose()
    {
        if (_disposed) return;
        _disposed = true;
        Queue.CompleteAdding();
        try { WriterThread.Join(500); } catch { }
    }
}
