using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace NaiPet {
  // Opt-in observation only: no network, no timer-resolution changes, no forced GC.
  // RecordDecode may run on the decoder thread; all mutable statistics use one gate.
  internal static class PerformanceProbe {
    internal static volatile bool Enabled;
    private static readonly object gate = new object();
    private static readonly Stopwatch elapsed = new Stopwatch();
    private static readonly CultureInfo invariant = CultureInfo.InvariantCulture;
    private static readonly Samples tickGap = new Samples();
    private static readonly Samples tickWork = new Samples();
    private static readonly Samples render = new Samples();
    private static readonly Samples decode = new Samples();
    private static string outputRoot;
    private static DateTime startedUtc;
    private static bool started, haveProcessSample, haveGuiSample;
    private static double nextResourceSample, cpuStartMs, cpuEndMs;
    private static long hits, misses, tickGapsOver50, cacheBytes, peakCacheBytes;
    private static int cacheEntries, peakCacheEntries, pending, peakPending;
    private static long privateStart, privateEnd, privatePeak, managedEnd, managedPeak;
    private static uint gdiStart, gdiEnd, gdiPeak, userStart, userEnd, userPeak;
    private static int processSamples, guiSamples, sampleErrors;
    private static string lastError = "";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    // Fixed-size recent ring, plus whole-run count, sum and maximum. Percentiles are
    // explicitly recent-window values, not a misleading percentile for the full run.
    private sealed class Samples {
      private const int Capacity = 4096;
      private readonly double[] values = new double[Capacity];
      private int used, position;
      private long count;
      private double total, maximum;
      internal void Clear() { used = position = 0; count = 0; total = maximum = 0; }
      internal void Add(double value) {
        if (Double.IsNaN(value) || Double.IsInfinity(value) || value < 0) return;
        values[position] = value; position = (position + 1) % Capacity;
        if (used < Capacity) used++;
        count++; total += value; maximum = Math.Max(maximum, value);
      }
      internal string Report(string name) {
        if (used == 0) return name + ": no samples";
        double[] sorted = new double[used];
        Array.Copy(values, sorted, used); Array.Sort(sorted);
        return String.Format(invariant,
          "{0}: whole-run count={1}, mean={2:F3} ms, max={3:F3} ms; latest {4} samples p50={5:F3} ms, p95={6:F3} ms, p99={7:F3} ms",
          name, count, total / count, maximum, used,
          Percentile(sorted, 0.50), Percentile(sorted, 0.95), Percentile(sorted, 0.99));
      }
      private static double Percentile(double[] sorted, double q) {
        return sorted[Math.Max(0, Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * q) - 1))];
      }
    }

    internal static void Start(string root) {
      if (!Enabled) return;
      try {
        string full = Path.GetFullPath(root);
        lock (gate) {
          outputRoot = full; startedUtc = DateTime.UtcNow;
          tickGap.Clear(); tickWork.Clear(); render.Clear(); decode.Clear();
          hits = misses = tickGapsOver50 = cacheBytes = peakCacheBytes = 0;
          cacheEntries = peakCacheEntries = pending = peakPending = 0;
          privateStart = privateEnd = privatePeak = managedEnd = managedPeak = 0;
          gdiStart = gdiEnd = gdiPeak = userStart = userEnd = userPeak = 0;
          processSamples = guiSamples = sampleErrors = 0; lastError = "";
          haveProcessSample = haveGuiSample = false;
          cpuStartMs = cpuEndMs = nextResourceSample = 0;
          elapsed.Reset(); elapsed.Start(); started = true;
        }
        ReadResources(true);
      } catch (Exception ex) { RecordError(ex); }
    }

    internal static void RecordTick(double gapSeconds, double workMilliseconds) {
      if (!Enabled) return;
      lock (gate) {
        if (!started) return;
        tickGap.Add(gapSeconds * 1000); tickWork.Add(workMilliseconds);
        if (!Double.IsNaN(gapSeconds) && !Double.IsInfinity(gapSeconds) && gapSeconds > 0.050) tickGapsOver50++;
      }
    }
    internal static void RecordDecode(double milliseconds) {
      if (!Enabled) return;
      lock (gate) { if (started) decode.Add(milliseconds); }
    }
    internal static void RecordRender(double milliseconds) {
      if (!Enabled) return;
      lock (gate) { if (started) render.Add(milliseconds); }
    }
    internal static void RecordCache(bool hit) {
      if (!Enabled) return;
      lock (gate) { if (!started) return; if (hit) hits++; else misses++; }
    }
    internal static void RecordCacheState(long bytes, int entries, int queue) {
      if (!Enabled) return;
      lock (gate) {
        if (!started) return;
        cacheBytes = bytes; cacheEntries = entries; pending = queue;
        peakCacheBytes = Math.Max(peakCacheBytes, bytes);
        peakCacheEntries = Math.Max(peakCacheEntries, entries);
        peakPending = Math.Max(peakPending, queue);
      }
    }
    internal static void SampleResources() {
      if (!Enabled) return;
      ReadResources(false);
    }
    private static void ReadResources(bool force) {
      try {
        lock (gate) {
          if (!started || (!force && elapsed.Elapsed.TotalSeconds < nextResourceSample)) return;
          nextResourceSample = elapsed.Elapsed.TotalSeconds + 1.0;
        }
        // Do not hold the statistics gate during OS queries.
        using (Process process = Process.GetCurrentProcess()) {
          process.Refresh();
          double cpu = process.TotalProcessorTime.TotalMilliseconds;
          long memory = process.PrivateMemorySize64, managed = GC.GetTotalMemory(false);
          lock (gate) {
            if (!haveProcessSample) { cpuStartMs = cpu; privateStart = memory; haveProcessSample = true; }
            cpuEndMs = cpu; privateEnd = memory; privatePeak = Math.Max(privatePeak, memory);
            managedEnd = managed; managedPeak = Math.Max(managedPeak, managed); processSamples++;
          }
          uint gdi = GetGuiResources(process.Handle, 0), users = GetGuiResources(process.Handle, 1);
          lock (gate) {
            if (!haveGuiSample) { gdiStart = gdi; userStart = users; haveGuiSample = true; }
            gdiEnd = gdi; gdiPeak = Math.Max(gdiPeak, gdi);
            userEnd = users; userPeak = Math.Max(userPeak, users); guiSamples++;
          }
        }
      } catch (Exception ex) { RecordError(ex); }
    }
    private static void RecordError(Exception error) {
      lock (gate) { sampleErrors++; lastError = error.GetType().Name + ": " + error.Message; }
    }
    internal static string Save() {
      if (!Enabled) return "性能诊断未启用；普通启动不写性能报告。";
      try {
        ReadResources(true);
        string destination, report;
        lock (gate) {
          if (!started) return "性能诊断未启动，未写报告。";
          DateTime saved = DateTime.UtcNow;
          destination = Path.Combine(outputRoot, "performance-report-" + saved.ToString("yyyyMMddTHHmmssfffZ", invariant) + ".txt");
          double seconds = elapsed.Elapsed.TotalSeconds;
          StringBuilder text = new StringBuilder();
          text.AppendLine("Naijia Desktop Pet Core geometric demo - observed Windows process metrics");
          text.AppendLine("NOT an automatic PASS, benchmark comparison or guarantee of smoothness.");
          text.AppendLine("Start UTC: " + startedUtc.ToString("o", invariant));
          text.AppendLine("Saved UTC: " + saved.ToString("o", invariant));
          text.AppendLine(String.Format(invariant, "Observed elapsed: {0:F3} seconds", seconds));
          text.AppendLine("OS: " + Environment.OSVersion + "; CLR: " + Environment.Version + "; process bits: " + (IntPtr.Size * 8));
          text.AppendLine("Logical processors: " + Environment.ProcessorCount.ToString(invariant));
          text.AppendLine();
          text.AppendLine(tickGap.Report("UI timer callback gap"));
          text.AppendLine("Whole-run UI timer gaps > 50 ms: " + tickGapsOver50.ToString(invariant));
          text.AppendLine(tickWork.Report("UI timer work"));
          text.AppendLine(render.Report("Presented-frame rendering work"));
          text.AppendLine(decode.Report("PNG decoding work (worker except initial idle preparation)"));
          text.AppendLine("Time samples are in milliseconds. Percentiles use only the latest up to 4096 samples per category.");
          text.AppendLine("Timer gaps include Windows scheduling delays and menu/modal interactions; not every gap is a skipped artwork frame.");
          text.AppendLine("Render timing is CPU-side work, not compositor/display latency or a direct visual FPS measurement.");
          text.AppendLine();
          text.AppendLine(String.Format(invariant, "Ready-frame lookups: hits={0}, misses={1}; these are lookups, not unique frames", hits, misses));
          text.AppendLine(String.Format(invariant, "Decoded cache bytes: current={0}, peak={1}", cacheBytes, peakCacheBytes));
          text.AppendLine(String.Format(invariant, "Decoded cache entries: current={0}, peak={1}; queued decode requests: current={2}, peak={3}", cacheEntries, peakCacheEntries, pending, peakPending));
          if (haveProcessSample) {
            double cpuSeconds = Math.Max(0, cpuEndMs - cpuStartMs) / 1000.0;
            double oneCore = seconds > 0 ? cpuSeconds / seconds * 100.0 : 0;
            text.AppendLine(String.Format(invariant, "CPU consumed: {0:F3} seconds; average one-core equivalent={1:F2}%; normalized across {2} logical processors={3:F2}%", cpuSeconds, oneCore, Environment.ProcessorCount, oneCore / Math.Max(1, Environment.ProcessorCount)));
            text.AppendLine(String.Format(invariant, "Process private bytes: start={0}, latest={1}, sampled peak={2}", privateStart, privateEnd, privatePeak));
            text.AppendLine(String.Format(invariant, "Managed heap bytes (no forced GC): latest={0}, sampled peak={1}", managedEnd, managedPeak));
          } else text.AppendLine("CPU/memory: unavailable");
          if (haveGuiSample) {
            text.AppendLine(String.Format(invariant, "GDI objects: start={0}, latest={1}, sampled peak={2}", gdiStart, gdiEnd, gdiPeak));
            text.AppendLine(String.Format(invariant, "USER objects: start={0}, latest={1}, sampled peak={2}", userStart, userEnd, userPeak));
          } else text.AppendLine("GDI/USER objects: unavailable");
          text.AppendLine(String.Format(invariant, "Resource samples: process={0}, GUI={1}, errors={2}", processSamples, guiSamples, sampleErrors));
          text.AppendLine("Resource peaks are sampled about once per second, so transient spikes can be missed.");
          text.AppendLine("Cache bytes count decoded pixels only; process memory also includes GDI surfaces, codecs, source buffers, CLR and PowerShell.");
          if (lastError.Length > 0) text.AppendLine("Last diagnostic error: " + lastError);
          text.AppendLine();
          text.AppendLine("Privacy: local opt-in report only; no network transmission or user-file scan.");
          text.AppendLine("Record pet count, roles, sizes, scenario and visible stutters when comparing reports on the same PC.");
          report = text.ToString();
        }
        // One write on explicit export/exit, never a per-frame file write.
        File.WriteAllText(destination, report, new UTF8Encoding(true));
        return destination;
      } catch (Exception ex) {
        RecordError(ex);
        return "性能报告未保存（桌宠可继续运行）：" + ex.Message;
      }
    }
  }
}
