using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ForzaNext.Core.Timing;

// A per-worker timer avoids changing the system-wide multimedia timer period.
public sealed class LoopTimer : IDisposable
{
    private sealed class NativeWait : WaitHandle { public NativeWait(SafeWaitHandle handle) => SafeWaitHandle = handle; }
    private readonly NativeWait? _timer;
    private WaitHandle[]? _waits;
    private CancellationToken _waitToken;
    public bool IsHighResolution { get; }
    public string Mode => IsHighResolution ? "Timer de alta resolução" : "Espera padrão do Windows";
    public LoopTimer()
    {
        var handle = CreateWaitableTimerExW(IntPtr.Zero, null, 2, 0x001F0003);
        if (!handle.IsInvalid) { _timer = new(handle); IsHighResolution = true; }
        else handle.Dispose();
    }
    public void WaitUntil(long deadline, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long remaining = deadline - Stopwatch.GetTimestamp();
        if (remaining <= 0) return;
        if (_timer != null)
        {
            long due = -Math.Max(1, (long)(remaining * (10_000_000.0 / Stopwatch.Frequency)));
            if (SetWaitableTimerEx(_timer.SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
            {
                if (_waits == null || token != _waitToken)
                { _waitToken = token; _waits = new[] { _timer, token.WaitHandle }; }
                if (WaitHandle.WaitAny(_waits) == 1) token.ThrowIfCancellationRequested();
                return;
            }
        }
        if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency))) token.ThrowIfCancellationRequested();
    }
    public void Dispose() => _timer?.Dispose();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(SafeWaitHandle handle, ref long due, int period, IntPtr callback, IntPtr argument, IntPtr context, uint tolerance);
}

public sealed record LoopBenchmark(string Mode, int Samples, double FrequencyHz, double MeanMs, double P95Ms, double P99Ms, double MaxMs)
{
    public static LoopBenchmark Summarize(string mode, IReadOnlyList<double> intervals)
    {
        if (intervals.Count == 0 || intervals.Any(v => !double.IsFinite(v) || v <= 0)) throw new ArgumentException("Intervalos inválidos.");
        var sorted = intervals.OrderBy(v => v).ToArray();
        double mean = sorted.Average();
        double Percentile(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new(mode, sorted.Length, 1000 / mean, mean, Percentile(.95), Percentile(.99), sorted[^1]);
    }
    public static LoopBenchmark Run(int seconds = 3, bool legacy = false, CancellationToken token = default)
    {
        if (seconds is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(seconds));
        using var timer = new LoopTimer();
        var intervals = new List<double>(seconds * 1200);
        long start = Stopwatch.GetTimestamp(), previous = start;
        long period = Stopwatch.Frequency / 1000;
        long next = start + period;
        while (Stopwatch.GetTimestamp() - start < seconds * Stopwatch.Frequency)
        {
            if (legacy) { if (token.WaitHandle.WaitOne(1)) token.ThrowIfCancellationRequested(); }
            else timer.WaitUntil(next, token);
            long now = Stopwatch.GetTimestamp(); intervals.Add((now - previous) * 1000.0 / Stopwatch.Frequency); previous = now;
            next += period;
            if (next < now) next = now + period;
        }
        return Summarize(legacy ? "WaitOne(1 ms)" : timer.Mode, intervals);
    }
}
