using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Egoist.Voice.Services;

/// <summary>Замеры одного распознавания для строки лога. Текста и звука здесь нет.</summary>
internal readonly record struct RecognitionBreakdown(
    double AudioSeconds,
    int Chunks,
    double QueueMs,
    double WarmMs,
    double PlanMs,
    double DecodeMs,
    double PrimaryMs,
    double FormatterExtraMs,
    double CpuMs,
    double SystemBusyPercent,
    long PageFaults,
    double WorkingSetMb,
    double GcPauseMs)
{
    /// <summary>Доля процессорного времени процесса от времени decode (около 1 и больше — декодер получает CPU).</summary>
    internal double CpuRatio => DecodeMs > 0 ? CpuMs / DecodeMs : 0;
}

internal static class RecognitionDiagnostics
{
    /// <summary>Одна строка лога со всеми полями; числа с точкой независимо от региональных настроек.</summary>
    internal static string Format(RecognitionBreakdown b)
    {
        var c = CultureInfo.InvariantCulture;
        return string.Create(c,
            $"Recognition breakdown: audio={b.AudioSeconds:0.00}s chunks={b.Chunks} queue={b.QueueMs:0}ms " +
            $"warm={b.WarmMs:0}ms plan={b.PlanMs:0}ms decode={b.DecodeMs:0}ms primary={b.PrimaryMs:0}ms " +
            $"formatterExtra={b.FormatterExtraMs:0}ms cpu={b.CpuMs:0}ms cpuRatio={b.CpuRatio:0.00} " +
            $"sysBusy={b.SystemBusyPercent:0}% faults={b.PageFaults} ws={b.WorkingSetMb:0}MB gcPause={b.GcPauseMs:0}ms");
    }

    /// <summary>Снимок счётчиков процесса и системы; разность двух снимков даёт часть строки. Стоит микросекунды.</summary>
    internal readonly struct Snapshot
    {
        internal long Timestamp { get; init; }
        internal double ProcessCpuMs { get; init; }
        internal ulong SystemIdle { get; init; }
        internal ulong SystemTotal { get; init; }
        internal long PageFaults { get; init; }
        internal double WorkingSetMb { get; init; }
        internal double GcPauseMs { get; init; }
    }

    internal static Snapshot Capture()
    {
        double cpuMs = 0;
        if (GetProcessTimes(GetCurrentProcess(), out _, out _, out var kernel, out var user))
            cpuMs = (kernel + user) / 10_000d;

        ulong idle = 0, total = 0;
        if (GetSystemTimes(out var sysIdle, out var sysKernel, out var sysUser))
        {
            idle = sysIdle;
            total = sysKernel + sysUser; // kernel уже включает idle
        }

        long faults = 0;
        double wsMb = 0;
        var cb = (uint)Marshal.SizeOf<ProcessMemoryCounters>();
        if (GetProcessMemoryInfo(GetCurrentProcess(), out var counters, cb))
        {
            faults = counters.PageFaultCount;
            wsMb = (double)counters.WorkingSetSize / 1048576d;
        }

        return new Snapshot
        {
            Timestamp = Stopwatch.GetTimestamp(),
            ProcessCpuMs = cpuMs,
            SystemIdle = idle,
            SystemTotal = total,
            PageFaults = faults,
            WorkingSetMb = wsMb,
            GcPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds
        };
    }

    /// <summary>Загрузка системы в процентах между двумя снимками.</summary>
    internal static double SystemBusyPercent(Snapshot from, Snapshot to)
    {
        if (to.SystemTotal <= from.SystemTotal) return 0;
        var total = to.SystemTotal - from.SystemTotal;
        var idle = Math.Min(total, to.SystemIdle - from.SystemIdle);
        return 100d * (total - idle) / total;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        nint process, out ulong creation, out ulong exit, out ulong kernel, out ulong user);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(nint process, out ProcessMemoryCounters counters, uint size);
}
