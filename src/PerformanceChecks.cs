using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NaiPet {
  // Test-only observation and a deterministic decoder latch. In normal execution
  // regressionGate is null; the DecodeLoop hook therefore performs only a null check.
  internal sealed partial class RoleCatalog {
    internal sealed class RegressionGate : IDisposable {
      internal readonly PetRole Role;
      internal readonly string Path;
      internal readonly ManualResetEvent Entered = new ManualResetEvent(false);
      internal readonly ManualResetEvent Proceed = new ManualResetEvent(false);
      internal int Claimed;
      internal bool FailAfterProceed;
      internal RegressionGate(PetRole role, string path) { Role = role; Path = path; }
      public void Dispose() { Entered.Dispose(); Proceed.Dispose(); }
    }
    internal sealed class RegressionState {
      internal long Bytes;
      internal int Allocated, Indexed, Lru, Pending, Queued, Generation, Workers;
      internal bool Stopping;
    }
    private volatile RegressionGate regressionGate;
    private void TestBeforeDecode(PetRole role, string path) {
      RegressionGate barrier = regressionGate;
      if (barrier == null) return;
      if (barrier.Role != role || !String.Equals(barrier.Path, path, StringComparison.OrdinalIgnoreCase) ||
          Interlocked.CompareExchange(ref barrier.Claimed, 1, 0) != 0) return;
      barrier.Entered.Set();
      // Never leave a diagnostic latch capable of blocking a worker indefinitely.
      if (!barrier.Proceed.WaitOne(30000)) throw new TimeoutException("回归测试解码门闩等待超时。");
      if (barrier.FailAfterProceed) throw new System.IO.IOException("Injected regression-only decode failure.");
    }
    internal RegressionGate TestArmDecode(PetRole role, string path) {
      lock (cacheGate) {
        if (regressionGate != null) throw new InvalidOperationException("A regression gate is already armed.");
        regressionGate = new RegressionGate(role, path); return regressionGate;
      }
    }
    internal RegressionState TestState(PetRole role) {
      lock (cacheGate) {
        RegressionState state = new RegressionState();
        state.Bytes = pixelBytes; state.Allocated = allocatedEntries; state.Indexed = images.Count;
        state.Lru = lru.Count; state.Pending = pending.Count; state.Queued = work.Count;
        state.Generation = Generation(role); state.Workers = decoder != null && decoder.IsAlive ? 1 : 0;
        state.Stopping = stopping; return state;
      }
    }
    internal bool TestContains(string path) { lock (cacheGate) { return images.ContainsKey(path); } }
    internal int TestForwardWindow(PetRole role) { lock (cacheGate) { return ForwardWindow(role); } }
    internal string[] TestQueuedPaths() {
      lock (cacheGate) {
        System.Collections.Generic.List<string> paths = new System.Collections.Generic.List<string>();
        foreach (FrameRequest request in work) paths.Add(request.Path);
        return paths.ToArray();
      }
    }
    internal void TestRequest(PetRole role, string path) { lock (cacheGate) { Enqueue(role, path, false); } }
    internal bool TestJoinDecoder(int milliseconds) {
      Thread worker; lock (cacheGate) { worker = decoder; }
      return worker == null || worker.Join(milliseconds); // Harness only: app disposal never joins on the UI thread.
    }
  }

  // Actual C#/GDI regression checks, run only by the dedicated Windows test command.
  // They do not start pets, alter source assets or claim a timing/visual benchmark.
  public static class PerformanceChecks {
    private const int WaitBudgetMilliseconds = 30000;
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);

    public static string Run(string root) {
      StringBuilder report = new StringBuilder();
      report.AppendLine("Geometric demo Windows C#/GDI regression checks");
      report.AppendLine("这不是 FPS/性能基准，也不验证透明窗口实际呈现、声音或多屏交互。");
      if (Environment.OSVersion.Platform != PlatformID.Win32NT) {
        report.AppendLine("NOT RUN: 本检查仅支持 Windows，未执行缓存或 GDI 测试。"); return report.ToString();
      }
      int passed = 0, failed = 0;
      Check(report, "初始待机同步准备及动画异步取帧", delegate { return InitialAndAsync(root); }, ref passed, ref failed);
      Check(report, "84 帧淘汰压力、活跃租约、共享角色及最终释放", delegate { return CacheLifecycle(root); }, ref passed, ref failed);
      Check(report, "确定性 in-flight Release 代际隔离", delegate { return ReleaseGeneration(root); }, ref passed, ref failed);
      Check(report, "64 项队列上限及 pending 状态退出", delegate { return PendingAndShutdown(root); }, ref passed, ref failed);
      Check(report, "待机帧成功发布与失败请求竞态恢复", delegate { return IdleFailureRecovery(root); }, ref passed, ref failed);
      Check(report, "急需帧公平排队、预取保护及动作预热", delegate { return Scheduling(root); }, ref passed, ref failed);
      Check(report, "100 次 DIB 创建/绘制/释放及句柄增长守卫", delegate { return SurfaceLifecycle(); }, ref passed, ref failed);
      report.AppendLine("结果：" + passed + " 项 PASS，" + failed + " 项 FAIL。");
      report.AppendLine("等待超时只表示测试未完成，不是已量化的性能回归。所有 PASS 均限于上述检查。");
      report.AppendLine("未运行项：UpdateLayeredWindow 实际呈现、视觉质量、音频、鼠标/多屏/全屏及长期压力。请按性能验证说明另测。");
      return report.ToString();
    }
    private static void Check(StringBuilder report, string name, Func<string> action, ref int passed, ref int failed) {
      try { string details = action(); passed++; report.AppendLine("PASS " + name + "：" + details); }
      catch (Exception ex) { failed++; report.AppendLine("FAIL " + name + "：" + ex.GetType().Name + " - " + ex.Message); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static int Remaining(Stopwatch watch) {
      int left = WaitBudgetMilliseconds - (int)Math.Min(Int32.MaxValue, watch.ElapsedMilliseconds);
      if (left <= 0) throw new TimeoutException("30 秒有界等待预算已用完；没有据此计算 FPS。");
      return left;
    }
    private static RoleCatalog.FrameLease Await(RoleCatalog catalog, PetRole role, string path, Stopwatch watch) {
      while (true) {
        Remaining(watch);
        RoleCatalog.FrameLease lease = catalog.TryAcquire(role, path);
        if (lease != null) return lease;
        Thread.Sleep(2); // Test harness only; the rendering path does not sleep or wait for decoding.
      }
    }
    private static void Bounds(RoleCatalog.RegressionState state) {
      Require(state.Bytes >= 0 && state.Bytes <= RoleCatalog.CacheByteLimit, "解码像素超出 40 MiB 上限。");
      Require(state.Allocated >= 0 && state.Allocated <= RoleCatalog.CacheEntryLimit, "位图数量超出 60 张上限。");
      Require(state.Pending >= 0 && state.Pending <= RoleCatalog.PendingLimit, "等待队列超出 64 项上限。");
      Require(state.Pending == state.Queued, "等待字典/队列不一致。");
      Require(state.Indexed == state.Lru && state.Indexed <= state.Allocated, "LRU/索引/分配计数不一致。");
    }
    private static string InitialAndAsync(string root) {
      Stopwatch watch = Stopwatch.StartNew(); RoleCatalog catalog = new RoleCatalog(root);
      try {
        PetRole role = catalog.Default; role.Users = 1; catalog.PrepareIdle(role);
        using (RoleCatalog.FrameLease idle = catalog.TryAcquire(role, role.Idle)) {
          Require(idle != null, "PrepareIdle 后未取得待机帧。");
          Require(idle.Image.Width == role.Width && idle.Image.Height == role.Height, "待机尺寸不符。");
          idle.Image.GetPixel(0, 0);
        }
        using (RoleCatalog.FrameLease action = Await(catalog, role, role.Laugh.Paths[0], watch)) {
          Require(action.Image.Width == role.Width && action.Image.Height == role.Height, "动画尺寸不符。");
          action.Image.GetPixel(0, 0);
        }
        Bounds(catalog.TestState(role));
        return "真实 PNG 解码后可读取像素；TryAcquire 最终获得后台准备的帧。";
      } finally {
        catalog.Dispose(); Require(catalog.TestJoinDecoder(Math.Max(1, WaitBudgetMilliseconds - (int)watch.ElapsedMilliseconds)), "解码线程未在预算内结束。");
      }
    }
    private static string CacheLifecycle(string root) {
      Stopwatch watch = Stopwatch.StartNew(); RoleCatalog catalog = new RoleCatalog(root);
      RoleCatalog.FrameLease pin = null;
      try {
        PetRole role = catalog.Default; role.Users = 2; catalog.PrepareIdle(role);
        pin = catalog.TryAcquire(role, role.Idle); Require(pin != null, "待机租约缺失。");
        int original = pin.Image.GetPixel(0, 0).ToArgb(), peak = 0;
        for (int i = 0; i < 84; i++) {
          using (RoleCatalog.FrameLease frame = Await(catalog, role, role.Laugh.Paths[i], watch)) { frame.Image.GetPixel(0, 0); }
          Require(pin.Image.GetPixel(0, 0).ToArgb() == original, "被固定的位图被修改或提前释放。");
          RoleCatalog.RegressionState sample = catalog.TestState(role); Bounds(sample); peak = Math.Max(peak, sample.Allocated);
        }
        Require(peak == RoleCatalog.CacheEntryLimit, "未实际填满缓存，未覆盖淘汰。");
        Require(!catalog.TestContains(role.Laugh.Paths[0]), "旧帧未被淘汰，压力路径未生效。");
        catalog.Release(role); Require(role.Users == 1, "共享角色计数错误。");
        using (RoleCatalog.FrameLease shared = catalog.TryAcquire(role, role.Idle)) {
          Require(shared != null && Object.ReferenceEquals(shared.Image, pin.Image), "仍有用户时共享待机资源被释放。");
        }
        catalog.Release(role); Require(role.Users == 0, "最终角色计数错误。");
        RoleCatalog.RegressionState retired = catalog.TestState(role); Bounds(retired);
        Require(retired.Indexed == 0 && retired.Allocated == 1, "最终 Release 未移除索引，或提前释放活跃租约。");
        Require(retired.Bytes == (long)role.Width * role.Height * 4, "保留租约字节计数错误。");
        Require(pin.Image.GetPixel(0, 0).ToArgb() == original, "Release 后活跃租约失效。");
        pin.Dispose(); pin = null;
        RoleCatalog.RegressionState empty = catalog.TestState(role); Bounds(empty);
        Require(empty.Allocated == 0 && empty.Bytes == 0, "最后租约结束后资源未清空。");
        return "已请求 84 张不同帧、达到 60 张并发生淘汰；最终租约释放后像素/分配计数为零。";
      } finally {
        if (pin != null) pin.Dispose(); catalog.Dispose();
        Require(catalog.TestJoinDecoder(Math.Max(1, WaitBudgetMilliseconds - (int)watch.ElapsedMilliseconds)), "淘汰测试后解码线程未结束。");
      }
    }
    private static string ReleaseGeneration(string root) {
      Stopwatch watch = Stopwatch.StartNew(); RoleCatalog catalog = new RoleCatalog(root);
      RoleCatalog.RegressionGate barrier = null;
      try {
        PetRole role = catalog.Default; role.Users = 1;
        string stale = role.Laugh.Paths[90], next = role.Laugh.Paths[91];
        barrier = catalog.TestArmDecode(role, stale);
        catalog.TestRequest(role, stale);
        Require(barrier.Entered.WaitOne(Remaining(watch)), "未进入确定性解码门闩。");
        int before = catalog.TestState(role).Generation;
        catalog.Release(role);
        Require(catalog.TestState(role).Generation == before + 1, "Release 未提升代际。");
        barrier.Proceed.Set(); role.Users = 1;
        using (RoleCatalog.FrameLease sentinel = Await(catalog, role, next, watch)) { sentinel.Image.GetPixel(0, 0); }
        // A single worker must finish the previously blocked request before the sentinel.
        Require(!catalog.TestContains(stale), "旧代际的 in-flight 解码结果重新插入缓存。");
        Bounds(catalog.TestState(role));
        return "门闩确认已出队请求；Release 后旧结果未插入，新代际哨兵帧可用。";
      } finally {
        if (barrier != null) barrier.Proceed.Set(); catalog.Dispose();
        bool joined = catalog.TestJoinDecoder(Math.Max(1, WaitBudgetMilliseconds - (int)watch.ElapsedMilliseconds));
        if (joined && barrier != null) barrier.Dispose();
        Require(joined, "代际测试后解码线程未结束。");
      }
    }
    private static string PendingAndShutdown(string root) {
      Stopwatch watch = Stopwatch.StartNew(); RoleCatalog catalog = new RoleCatalog(root);
      RoleCatalog.RegressionGate barrier = null;
      try {
        PetRole role = catalog.Default; role.Users = 1;
        barrier = catalog.TestArmDecode(role, role.Laugh.Paths[95]);
        catalog.TestRequest(role, role.Laugh.Paths[95]);
        Require(barrier.Entered.WaitOne(Remaining(watch)), "未进入退出测试门闩。");
        // The geometric demo supplies 96 distinct paths; more than the 64-slot queue.
        for (int i = 0; i < 95; i++) {
          catalog.TestRequest(role, role.Laugh.Paths[i]); Bounds(catalog.TestState(role));
        }
        RoleCatalog.RegressionState queued = catalog.TestState(role);
        Require(queued.Pending == RoleCatalog.PendingLimit && queued.Workers == 1, "未实际覆盖满队列和运行线程。");
        catalog.Dispose(); catalog.Dispose();
        RoleCatalog.RegressionState stopped = catalog.TestState(role); Bounds(stopped);
        Require(stopped.Stopping && stopped.Pending == 0 && stopped.Indexed == 0, "Dispose 未清空等待工作。");
        Require(catalog.TryAcquire(role, role.Idle) == null, "Dispose 后仍取得帧。");
        barrier.Proceed.Set();
        Require(catalog.TestJoinDecoder(Remaining(watch)), "Dispose 后后台线程未结束。");
        RoleCatalog.RegressionState final = catalog.TestState(role); Bounds(final);
        Require(final.Workers == 0 && final.Allocated == 0 && final.Bytes == 0, "退出后仍有线程或解码像素。");
        return "200 个请求保持不超过 64 项；Dispose 可重复调用，解除门闩后线程退出且缓存为空。";
      } finally {
        if (barrier != null) barrier.Proceed.Set(); catalog.Dispose();
        bool joined = catalog.TestJoinDecoder(Math.Max(1, WaitBudgetMilliseconds - (int)watch.ElapsedMilliseconds));
        if (joined && barrier != null) barrier.Dispose();
        Require(joined, "退出测试清理失败。");
      }
    }
    private static string IdleFailureRecovery(string root) {
      // Both orderings use the actual Bitmap cache. No source file is changed.
      for (int prepareFirst = 0; prepareFirst < 2; prepareFirst++) {
        Stopwatch watch = Stopwatch.StartNew(); RoleCatalog catalog = new RoleCatalog(root);
        RoleCatalog.RegressionGate barrier = null;
        try {
          PetRole role = catalog.Default; role.Users = 1;
          barrier = catalog.TestArmDecode(role, role.Idle); barrier.FailAfterProceed = true;
          catalog.TestRequest(role, role.Idle);
          Require(barrier.Entered.WaitOne(Remaining(watch)), "待机请求未进入门闩。");
          if (prepareFirst == 1) catalog.PrepareIdle(role);
          barrier.Proceed.Set();
          using (RoleCatalog.FrameLease sentinel = Await(catalog, role, role.Laugh.Paths[0], watch)) { sentinel.Image.GetPixel(0, 0); }
          // A single worker completed the forced failure before this sentinel.
          if (prepareFirst == 0) {
            bool failed = false;
            try { using (RoleCatalog.FrameLease ignored = catalog.TryAcquire(role, role.Idle)) { } }
            catch (System.IO.InvalidDataException) { failed = true; }
            Require(failed, "未观察到准备之前的注入失败。");
            catalog.PrepareIdle(role);
          }
          using (RoleCatalog.FrameLease idle = catalog.TryAcquire(role, role.Idle)) {
            Require(idle != null, "成功准备的待机图被失败状态遮蔽。"); idle.Image.GetPixel(0, 0);
          }
          Bounds(catalog.TestState(role));
        } finally {
          if (barrier != null) barrier.Proceed.Set(); catalog.Dispose();
          bool joined = catalog.TestJoinDecoder(Math.Max(1, WaitBudgetMilliseconds - (int)watch.ElapsedMilliseconds));
          if (joined && barrier != null) barrier.Dispose();
          Require(joined, "待机失败竞态测试后线程未结束。");
        }
      }
      return "已覆盖成功准备后迟到失败，以及先失败再成功准备；真实待机位图均可读取。";
    }
    private static string Scheduling(string root) {
      Stopwatch watch = Stopwatch.StartNew(); RoleCatalog catalog = new RoleCatalog(root);
      RoleCatalog.RegressionGate barrier = null;
      try {
        PetRole role = catalog.Default; role.Users = 1;
        barrier = catalog.TestArmDecode(role, role.Idle); catalog.TestRequest(role, role.Idle);
        Require(barrier.Entered.WaitOne(Remaining(watch)), "调度测试未进入门闩。");
        for (int i = 0; i < 100; i++) using (RoleCatalog.FrameLease lease = catalog.TryAcquire(role, role.Idle)) {
          Require(lease == null, "阻塞的帧不应已准备好。");
        }
        Require(catalog.TestState(role).Pending == 0, "正在解码的同代次帧重复排队。");
        catalog.RequestIdle(role);
        string[] warmed = catalog.TestQueuedPaths();
        for (int i = 0; i < RoleCatalog.LookAhead; i++)
          Require(Array.IndexOf(warmed, role.Laugh.Paths[i]) >= 0, "未预热动作首批帧。");
        Require(Array.IndexOf(warmed, role.Laugh.Paths[RoleCatalog.LookAhead]) < 0, "预热超出窗口。");
        role.Users = 6;
        Require(catalog.TestForwardWindow(new PetRole { Width = 1024, Height = 1024 }) == 1, "大图多宠预取未按像素预算收缩。");
        Require(catalog.TestForwardWindow(role) == RoleCatalog.LookAhead, "默认小图预取被无故缩减。");
        role.Users = 1;
        // Promote an existing speculative frame between two fresh urgent requests.
        using (RoleCatalog.FrameLease lease = catalog.TryAcquire(role, role.Laugh.Paths[20])) { }
        using (RoleCatalog.FrameLease lease = catalog.TryAcquire(role, role.Laugh.Paths[0])) { }
        using (RoleCatalog.FrameLease lease = catalog.TryAcquire(role, role.Laugh.Paths[21])) { }
        string[] ordered = catalog.TestQueuedPaths();
        Require(ordered[0] == role.Laugh.Paths[20] && ordered[1] == role.Laugh.Paths[0] && ordered[2] == role.Laugh.Paths[21], "急需帧或晋升帧不遵守FIFO。");
        for (int i = 22; i < 83; i++) using (RoleCatalog.FrameLease lease = catalog.TryAcquire(role, role.Laugh.Paths[i])) { }
        string before = String.Join("\n", catalog.TestQueuedPaths());
        Require(catalog.TestState(role).Pending == RoleCatalog.PendingLimit, "未覆盖全急需满队列。");
        catalog.TestRequest(role, role.Laugh.Paths[90]);
        Require(String.Join("\n", catalog.TestQueuedPaths()) == before, "预取挤掉急需帧。");
        using (RoleCatalog.FrameLease lease = catalog.TryAcquire(role, role.Laugh.Paths[91])) { }
        Require(String.Join("\n", catalog.TestQueuedPaths()) == before, "全急需满队列不稳定。");
        Bounds(catalog.TestState(role));
        catalog.Release(role); role.Users = 1;
        using (RoleCatalog.FrameLease lease = catalog.TryAcquire(role, role.Idle)) { }
        Require(catalog.TestState(role).Pending == 1, "旧代次in-flight阻止了新代次同帧请求。");
        return "真实队列覆盖FIFO、预取保护、同代次去重、新代次重试及按像素预算的1至4帧预热；未测FPS。";
      } finally {
        // Cancel queued work before releasing the worker; no mass decoding is required.
        catalog.Dispose(); if (barrier != null) barrier.Proceed.Set();
        bool joined = catalog.TestJoinDecoder(Math.Max(1, WaitBudgetMilliseconds - (int)watch.ElapsedMilliseconds));
        if (joined && barrier != null) barrier.Dispose();
        Require(joined, "调度测试后线程未结束。");
      }
    }
    private static string SurfaceLifecycle() {
      Stopwatch watch = Stopwatch.StartNew();
      using (Bitmap sprite = new Bitmap(32, 32)) {
        using (Graphics g = Graphics.FromImage(sprite)) { g.Clear(Color.FromArgb(128, 60, 180, 90)); }
        using (LayeredSurface warmup = new LayeredSurface(220, 220)) { warmup.Draw(sprite, 180); }
        using (Process process = Process.GetCurrentProcess()) {
          uint before = GetGuiResources(process.Handle, 0), userBefore = GetGuiResources(process.Handle, 1);
          uint peak = before;
          for (int i = 0; i < 100; i++) {
            Remaining(watch); int size = i % 3 == 0 ? 120 : (i % 3 == 1 ? 180 : 260);
            LayeredSurface surface = new LayeredSurface(size + 40, size + 40);
            try {
              surface.Draw(sprite, size); surface.Draw(sprite, size);
              peak = Math.Max(peak, GetGuiResources(process.Handle, 0));
            } finally { surface.Dispose(); surface.Dispose(); }
            bool rejected = false;
            try { surface.Draw(sprite, size); } catch (ObjectDisposedException) { rejected = true; }
            Require(rejected, "Dispose 后仍允许绘制。");
          }
          uint after = GetGuiResources(process.Handle, 0), userAfter = GetGuiResources(process.Handle, 1);
          long delta = (long)after - before, userDelta = (long)userAfter - userBefore;
          Require(peak > before, "未观察到活动 DIB/DC 的 GDI 对象增长，句柄计数不可用于通过判断。");
          Require(delta <= 4, "100 次释放后 GDI 增长 " + delta + "，超出允许的 4 个惰性全局资源；需查泄漏。");
          Require(userDelta <= 2, "未创建窗口却新增 " + userDelta + " 个 USER 对象；需复查。");
          return "GDI 基线=" + before + "，结束=" + after + "，观察峰值=" + peak + "；USER 基线=" + userBefore + "，结束=" + userAfter + "。允许 GDI≤+4/USER≤+2 的惰性初始化余量，仅是粗粒度增长守卫，不证明零泄漏或视觉正确。";
        }
      }
    }
  }
}
