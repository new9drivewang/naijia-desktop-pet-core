using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Threading;
using System.Diagnostics;
using System.Media;
using System.Text;

namespace NaiPet {
  public static class Program {
    // Optional read-only package check. It does not start pets or write/delete user files.
    public static string CheckFiles(string root) {
      return PackageCheck.Verify(root);
    }
    public static void RunProfile(string root) { PerformanceProbe.Enabled = true; PerformanceProbe.Start(root); try { Run(root); } finally { PerformanceProbe.Save(); } }
    [STAThread]
    public static void Run(string root) {
      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);
      bool created;
      using (Mutex instance = new Mutex(true, "Local\\NaijiaDesktopPetCoreDemo_v1", out created)) {
        if (!created) { MessageBox.Show("桌宠已经启动。请从右下角托盘菜单操作，或双击“Stop.cmd”。", "几何桌宠演示"); return; }
        try { using (PetContext context = new PetContext(root)) Application.Run(context); }
        catch (Exception ex) { MessageBox.Show("桌宠启动失败：\n" + ex.Message, "几何桌宠演示", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { instance.ReleaseMutex(); }
      }
    }
  }
  public sealed class PetContext : ApplicationContext {
    public const int Limit = 6;
    internal readonly List<PetWindow> Pets = new List<PetWindow>();
    internal readonly Random Random = new Random();
    internal RoleCatalog Catalog;
    private PetSettings settings;
    private readonly string settingsDirectory;
    internal HoverPreferences HoverPreferences { get; private set; }
    private int hoverMenuDepth;
    internal bool HoverMenuOpen { get { return hoverMenuDepth > 0; } }
    private void SuppressHover() { foreach (PetWindow pet in Pets) pet.SuppressHover(); }
    private void SetHoverPreferences(bool enabled, int cooldown) {
      if (HoverPreferences.Enabled == enabled && HoverPreferences.CooldownMilliseconds == cooldown) return;
      HoverPreferences = new HoverPreferences(enabled, cooldown); SuppressHover();
      try { HoverPreferenceStore.Save(settingsDirectory, HoverPreferences); }
      catch (Exception ex) { Notice("互动偏好未保存", "当前选择仍有效，下次可能恢复旧设置：" + ex.Message); }
    }
    internal void AddHoverMenu(ContextMenuStrip menu) {
      ToolStripMenuItem group = new ToolStripMenuItem("互动偏好（全部桌宠）");
      ToolStripMenuItem clicks = new ToolStripMenuItem("仅点击互动");
      ToolStripMenuItem hover = new ToolStripMenuItem("停留 400 毫秒摸肚");
      clicks.Click += delegate { SetHoverPreferences(false, HoverPreferences.CooldownMilliseconds); };
      hover.Click += delegate { SetHoverPreferences(true, HoverPreferences.CooldownMilliseconds); };
      group.DropDownItems.Add(clicks); group.DropDownItems.Add(hover);
      group.DropDownItems.Add(new ToolStripSeparator());
      ToolStripMenuItem cooldown = new ToolStripMenuItem("悬停摸肚冷却");
      int[] intervals = { 800, 2000, 5000 }; string[] labels = { "800 毫秒（默认）", "2 秒", "5 秒" };
      for (int i = 0; i < intervals.Length; i++) {
        int interval = intervals[i]; ToolStripMenuItem choice = new ToolStripMenuItem(labels[i]); choice.Tag = interval;
        choice.Click += delegate { SetHoverPreferences(HoverPreferences.Enabled, interval); };
        cooldown.DropDownItems.Add(choice);
      }
      group.DropDownItems.Add(cooldown);
      group.ToolTipText = "默认仅点击；开启后需在肚子停留 400 毫秒。嘴巴和肚子点击仍即时响应。冷却内经过不会稍后补触发，需离开再进入。";
      group.DropDownOpening += delegate {
        clicks.Checked = !HoverPreferences.Enabled; hover.Checked = HoverPreferences.Enabled;
        cooldown.Enabled = HoverPreferences.Enabled;
        foreach (ToolStripMenuItem choice in cooldown.DropDownItems) choice.Checked = (int)choice.Tag == HoverPreferences.CooldownMilliseconds;
      };
      menu.Items.Add(group);
      // Menus cancel pending pointer intentions, never the current visual/audio action.
      menu.Opening += delegate { SuppressHover(); foreach (PetWindow pet in Pets) pet.CancelPointerIntent(); };
      menu.Opened += delegate { hoverMenuDepth++; };
      menu.Closed += delegate { hoverMenuDepth = Math.Max(0, hoverMenuDepth - 1); SuppressHover(); };
    }
    internal bool RecoveryTransition { get { return settings.RecoveryTransition; } }
    private void SaveSettings() {
      settings.Sound = SoundEnabled; settings.AutoStand = AutoStand;
      try { LocalSettings.Save(settingsDirectory, settings); }
      catch (Exception ex) { Notice("本次设置未保存", "当前选择仍有效，下次可能恢复旧设置：" + ex.Message); }
    }
    internal void ToggleAutoStand() { AutoStand = !AutoStand; SaveSettings(); }
    internal void ToggleRecovery() { settings.RecoveryTransition = !settings.RecoveryTransition; SaveSettings(); }
    internal void RememberRole(PetRole role) { settings.PreferredRole = role.Id; SaveSettings(); }
    private readonly Stopwatch watch = new Stopwatch();
    private double previousTime;
    private readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer();
    private readonly EventWaitHandle exitSignal = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\NaijiaDesktopPetCoreDemo_Stop_v1");
    private readonly NotifyIcon tray = new NotifyIcon();
    private bool stopping, disposed;
    internal bool AutoStand;
    internal bool ThrowEnabled = true;
    internal bool FullscreenAvoidance;
    private double nextFullscreenCheck;
    internal void ToggleThrow() { ThrowEnabled = !ThrowEnabled; if (!ThrowEnabled) foreach (PetWindow pet in Pets) pet.CancelFlight(); }
    internal void ToggleFullscreen() { FullscreenAvoidance = !FullscreenAvoidance; nextFullscreenCheck = 0; UpdateAvoidance(); }
    private void UpdateAvoidance() {
      Rectangle? fullscreen = FullscreenAvoidance ? Native.ForegroundFullscreen(Pets) : null;
      foreach (PetWindow pet in Pets.ToArray()) {
        bool hide = fullscreen.HasValue && Screen.FromControl(pet).Bounds == fullscreen.Value;
        pet.SetAvoided(hide);
      }
    }
    internal void KeepOnly(PetWindow keep) {
      foreach (PetWindow pet in Pets.ToArray()) if (pet != keep) Remove(pet);
    }
    private SoundPlayer sound;
    private PetWindow soundOwner;
    internal bool SoundEnabled;
    internal bool Paused;
    internal void TogglePause() { Paused = !Paused; if (Paused) { StopAudio(null); foreach (PetWindow pet in Pets.ToArray()) if (!pet.IsDisposed) pet.CancelTransient(true); } }
    internal void ToggleSound() { SoundEnabled = !SoundEnabled; if (!SoundEnabled) StopAudio(null); SaveSettings(); }
    internal void PlayAudio(PetWindow owner) {
      if (!SoundEnabled || Paused || soundOwner != null || owner == null || owner.IsDisposed || owner.IsAvoided) return;
      if (String.IsNullOrEmpty(owner.Role.Audio) || !File.Exists(owner.Role.Audio)) return;
      try {
        if (sound != null) { sound.Dispose(); sound = null; }
        string audioPath = RoleCatalog.AssetPath(owner.Role, owner.Role.Audio, ".wav");
        if (new FileInfo(audioPath).Length > 20L * 1024 * 1024) return;
        sound = new SoundPlayer(audioPath); sound.Load(); soundOwner = owner; sound.Play();
      }
      catch (Exception ex) { if (sound != null) { sound.Dispose(); sound = null; } soundOwner = null; SoundEnabled = false; Notice("声音暂不可用", ex.Message); }
    }
    internal void StopAudio(PetWindow owner) {
      // Revoke pending starts before checking the current sound owner. A pet whose
      // first PNG is still decoding has no owner yet, but must not sound on resume.
      if (owner == null) { foreach (PetWindow pet in Pets) pet.SuppressActionAudio(); }
      else owner.SuppressActionAudio();
      if (owner != null && owner != soundOwner) return;
      if (sound != null) { try { sound.Stop(); } catch { SoundEnabled = false; } } soundOwner = null;
    }
    public PetContext(string root) : this(root, LocalSettings.DirectoryPath) { }
    internal PetContext(string root, string settingsDirectory) {
      this.settingsDirectory = settingsDirectory;
      try {
      exitSignal.Reset();
      string settingsWarning; settings = LocalSettings.Load(settingsDirectory, out settingsWarning);
      string hoverWarning; HoverPreferences = HoverPreferenceStore.Load(settingsDirectory, out hoverWarning);
      SoundEnabled = settings.Sound; AutoStand = settings.AutoStand;
      Catalog = new RoleCatalog(root);
      Catalog.ChooseStartup(settings.PreferredRole);
      if (settingsWarning != null) Catalog.Warnings.Add(settingsWarning);
      if (hoverWarning != null) Catalog.Warnings.Add(hoverWarning);
      ContextMenuStrip menu = new ContextMenuStrip();
      menu.Items.Add("再来一只（最多6只）", null, delegate { Spawn(null); });
      ToolStripMenuItem pause = new ToolStripMenuItem("全部暂停 / 继续（本轮声音不续播）");
      pause.Click += delegate { TogglePause(); };
      menu.Items.Add(pause);
      menu.Items.Add("召回所有桌宠", null, delegate { Recall(); });
      menu.Items.Add("添加几何占位角色（补齐角色，最多6只）", null, delegate { SummonMissing(Screen.FromPoint(Cursor.Position).WorkingArea); });
      ToolStripMenuItem avoidFull = new ToolStripMenuItem("全屏自动避让（恢复本轮静音，默认关）");
      avoidFull.Click += delegate { ToggleFullscreen(); avoidFull.Checked = FullscreenAvoidance; };
      menu.Items.Add(avoidFull);
      AddHoverMenu(menu);
      menu.Opening += delegate { avoidFull.Checked = FullscreenAvoidance; };
      if (Catalog.Warnings.Count > 0) menu.Items.Add("未加载角色的原因", null, delegate { MessageBox.Show(String.Join("\n", Catalog.Warnings.ToArray()), "角色加载提示"); });
      if (PerformanceProbe.Enabled) menu.Items.Add("导出性能诊断", null, delegate { Notice("性能诊断", PerformanceProbe.Save()); });
      menu.Items.Add("全部退出", null, delegate { Stop(); });
      tray.Icon = SystemIcons.Application;
      tray.Text = "几何桌宠演示 · 右键可全部退出";
      tray.ContextMenuStrip = menu;
      tray.Visible = true;
      tray.DoubleClick += delegate { Recall(); };
      Spawn(null);
      if (Catalog.Warnings.Count > 0) Notice("部分角色未加载", String.Join("\n", Catalog.Warnings.ToArray()));
      clock.Interval = 16; // Request frequent ticks; artwork advances on its original 30 fps timeline.
      watch.Start();
      clock.Tick += delegate {
        if (exitSignal.WaitOne(0)) { Stop(); return; }
        double now = watch.Elapsed.TotalSeconds;
        double dt = Math.Max(0, now - previousTime); previousTime = now;
        try {
          long tickStart = Stopwatch.GetTimestamp();
          if (now >= nextFullscreenCheck) {
            nextFullscreenCheck = now + 0.5;
            for (int i = 0; i < Pets.Count; i++) Pets[i].RefreshDisplayBounds();
            if (FullscreenAvoidance) UpdateAvoidance();
            PerformanceProbe.SampleResources();
          }
          for (int i = 0; i < Pets.Count; i++) if (!Pets[i].IsDisposed) Pets[i].Step(Paused, dt);
          PerformanceProbe.RecordTick(dt, (Stopwatch.GetTimestamp() - tickStart) * 1000.0 / Stopwatch.Frequency);
        }
        catch (Exception ex) { Stop(); MessageBox.Show("桌宠遇到绘制错误，已安全退出：\n" + ex.Message, "几何桌宠演示"); }
      };
      clock.Start();
      } catch { Dispose(); throw; }
    }
    internal void Notice(string title, string message) { tray.ShowBalloonTip(4000, title.Length > 63 ? title.Substring(0, 63) : title, message.Length > 240 ? message.Substring(0, 240) : message, ToolTipIcon.Info); }
    internal Point FindLanding(Rectangle work, Size size, Point preferred, PetWindow ignore) {
      List<Point> candidates = new List<Point>();
      candidates.Add(preferred);
      int strideX = size.Width + 12, strideY = size.Height + 12;
      for (int yy = work.Bottom - size.Height; yy >= work.Top; yy -= strideY)
        for (int xx = work.Right - size.Width; xx >= work.Left; xx -= strideX) candidates.Add(new Point(xx, yy));
      foreach (PetWindow other in Pets) {
        if (other == ignore || other.IsDisposed) continue;
        Rectangle occupied = other.LandingBounds;
        candidates.Add(new Point(occupied.Right + 12, occupied.Top));
        candidates.Add(new Point(occupied.Left - size.Width - 12, occupied.Top));
        candidates.Add(new Point(occupied.Left, occupied.Top - size.Height - 12));
        candidates.Add(new Point(occupied.Left, occupied.Bottom + 12));
      }
      Point best = preferred; long bestOverlap = long.MaxValue; double bestDistance = double.MaxValue;
      foreach (Point candidate in candidates) {
        Point at = new Point(Math.Max(work.Left, Math.Min(candidate.X, work.Right - size.Width)),
          Math.Max(work.Top, Math.Min(candidate.Y, work.Bottom - size.Height)));
        Rectangle bounds = new Rectangle(at, size); long overlap = 0;
        foreach (PetWindow other in Pets) {
          if (other == ignore || other.IsDisposed) continue;
          Rectangle padded = other.LandingBounds; padded.Inflate(6, 6);
          Rectangle hit = Rectangle.Intersect(bounds, padded); overlap += (long)hit.Width * hit.Height;
        }
        double dx = (double)at.X - preferred.X, dy = (double)at.Y - preferred.Y;
        double distance = dx * dx + dy * dy;
        if (overlap < bestOverlap || (overlap == bestOverlap && distance < bestDistance)) {
          best = at; bestOverlap = overlap; bestDistance = distance;
        }
      }
      return best;
    }
    internal void Spawn(PetWindow source) {
      if (stopping || (source != null && Paused)) return;
      if (Pets.Count >= Limit) { tray.ShowBalloonTip(2000, "已经有6只几何占位桌宠", "先收回一只，再分裂；不会继续增加。", ToolTipIcon.Info); return; }
      Rectangle work = source == null ? Screen.FromPoint(Cursor.Position).WorkingArea : Screen.FromControl(source).WorkingArea;
      PetRole initialRole = source == null ? Catalog.Default : source.Role;
      try { Catalog.PrepareIdle(initialRole); }
      catch (Exception ex) { Notice("无法创建此角色", initialRole.Name + "：" + ex.Message); return; }
      PetWindow pet = null;
      try {
        pet = new PetWindow(this, initialRole);
        Point preferred = source == null ? new Point(work.Right - 235, work.Bottom - 235) : source.Location;
        pet.StartAt(preferred, source == null ? 180 : source.PetSize);
        Point landing = FindLanding(work, pet.Size, preferred, null);
        if (source == null) pet.StartAt(landing, pet.PetSize);
        Pets.Add(pet); pet.Show();
        if (source != null) { pet.BeginSplit(source, landing); source.StartLaugh(); }
      } catch (Exception ex) {
        if (pet != null) { Pets.Remove(pet); pet.Dispose(); }
        Notice("无法创建桌宠", ex.Message);
      }
    }

    internal void SummonMissing(Rectangle area) {
      if (stopping) return;
      int added = 0, capacity = 0, space = 0, failed = 0;
      foreach (PetRole role in Catalog.Roles) {
        bool present = false; foreach (PetWindow existing in Pets) if (!existing.IsDisposed && existing.Role == role) present = true;
        if (present) continue;
        if (Pets.Count >= Limit) { capacity++; continue; }
        Size size = new Size(220, 220);
        Point preferred = new Point(area.Right - size.Width - 16, area.Bottom - size.Height - 16);
        Point landing = FindLanding(area, size, preferred, null);
        Rectangle bounds = new Rectangle(landing, size); bool blocked = !area.Contains(bounds);
        foreach (PetWindow existing in Pets) {
          Rectangle occupied = existing.LandingBounds; occupied.Inflate(6, 6);
          if (bounds.IntersectsWith(occupied)) blocked = true;
        }
        if (blocked) { space++; continue; }
        try { Catalog.PrepareIdle(role); } catch { failed++; continue; }
        PetWindow pet = null;
        try {
          pet = new PetWindow(this, role);
          pet.StartAt(landing, 180); Pets.Add(pet); pet.Show();
          if (pet.Role != role) { Remove(pet); failed++; continue; }
          added++;
        } catch {
          if (pet != null) { Pets.Remove(pet); pet.Dispose(); }
          failed++;
        }
      }
      UpdateAvoidance();
      if (added == 0 && capacity == 0 && space == 0 && failed == 0) { Notice("本包已加载角色已到齐", "已有每种已加载角色，不重复添加，也不替换现有分身。"); return; }
      string message = "新增" + added + "只；现有分身未移动、未替换，新角色不自动播放动作或声音。";
      if (capacity > 0 || space > 0 || failed > 0) message += " 尚未补齐：数量上限" + capacity + "种，当前屏空位不足" + space + "种，素材读取失败" + failed + "种。";
      Notice("添加几何占位角色", message);
    }
    internal void Remove(PetWindow pet) {
      Pets.Remove(pet);
      if (!pet.IsDisposed) pet.Close();
      if (Pets.Count == 0) Stop();
    }
    internal void WindowClosed(PetWindow pet) {
      StopAudio(pet); Pets.Remove(pet);
      if (!stopping && Pets.Count == 0) Stop();
    }
    internal void Recall() { Gather(Screen.FromPoint(Cursor.Position).WorkingArea); }
    internal void Gather(Rectangle area) {
      foreach (PetWindow pet in Pets.ToArray()) {
        Point landing = FindLanding(area, pet.Size, new Point(area.Left + 24, area.Bottom - pet.Height - 24), pet);
        pet.StartAt(landing, pet.PetSize);
      }
    }
    internal void Stop() {
      if (stopping) return;
      stopping = true;
      try { StopAudio(null); }
      finally {
        StopAudio(null); clock.Stop(); tray.Visible = false;
        foreach (PetWindow pet in Pets.ToArray()) pet.Close();
        Pets.Clear();
        ExitThread();
      }
    }
    protected override void Dispose(bool disposing) {
      try {
        if (disposing && !disposed) {
          disposed = true; stopping = true;
          try { StopAudio(null); }
          finally {
            clock.Stop();
            foreach (PetWindow pet in Pets.ToArray()) if (!pet.IsDisposed) pet.Close();
            Pets.Clear();
            StopAudio(null); if (sound != null) sound.Dispose();
            clock.Dispose(); exitSignal.Dispose(); tray.Visible = false; if (tray.ContextMenuStrip != null) tray.ContextMenuStrip.Dispose(); tray.Dispose();
            if (Catalog != null) Catalog.Dispose();
          }
        }
      } finally { base.Dispose(disposing); }
    }
  }
  public sealed class PetWindow : Form {
    private readonly PetContext app;
    private readonly ContextMenuStrip menu;
    internal PetRole Role { get; private set; }
    private bool dragging, moved, playing, resting, actionPending;
    private readonly GestureState gesture = new GestureState();
    private readonly VisualEffects effects = new VisualEffects();
    private readonly ActionAudioGate actionAudio = new ActionAudioGate();
    internal void SuppressActionAudio() { actionAudio.Suppress(); }
    private readonly HoverInteraction hoverInteraction = new HoverInteraction(delegate { return Now; });
    internal void SuppressHover() { hoverInteraction.Suppress(); }
    internal void CancelPointerIntent() { gesture.Cancel(); dragging = false; moved = true; Capture = false; }
    private bool HoverAllowed {
      get { return !app.Paused && !app.HoverMenuOpen && !avoided && Visible && !dragging &&
        !playing && !resting && !effects.Recovering && !splitting && !airborne; }
    }
    private double renderedElastic;
    private bool displayedStanding;
    private string displayedRoleId;
    private bool resetOpacity;
    internal void CancelTransient(bool redraw) {
      if (IsHandleCreated) SuppressHover();
      // Ordinary pause/hide preserves the last presented action pixel-for-pixel.
      // Only visual effects need an immediate redraw; a redraw of a pending action
      // could otherwise replace its last valid surface with fallback idle.
      bool visualChanged = effects.Recovering || effects.Bouncing || renderedElastic != 0;
      gesture.Cancel();
      if (effects.Recovering) {
        playing = resting = actionPending = false; laughElapsed = restElapsed = 0;
        resetOpacity = true;
      }
      effects.Cancel();
      if (visualChanged) {
        lastRenderKey = null;
        if (redraw && IsHandleCreated && !IsDisposed && !avoided) Render();
      }
    }
    private Point dragOffset, downAt;
    private float x, y;
    private double activeTime, laughElapsed, splitElapsed, restElapsed;
    private bool splitting;
    private float splitX, splitY, splitDX, splitDY;
    private string lastRenderKey;
    private LayeredSurface surface;
    private bool roleRegistered, resourcesReleased;
    private double nextBlink, blinkStarted = -100;
    private bool airborne, avoided, revealPending;
    internal bool IsAvoided { get { return avoided; } }
    private double velocityX, velocityY, dragSampleTime, releaseVX, releaseVY;
    private PointF dragSamplePoint;
    private struct DragSample { internal double Time; internal PointF At; internal DragSample(double time, PointF at) { Time = time; At = at; } }
    private readonly Queue<DragSample> dragHistory = new Queue<DragSample>();
    private void SampleDrag(double time) {
      if (time - dragSampleTime < 0.004) return;
      if (time - dragSampleTime > 0.12) dragHistory.Clear();
      dragHistory.Enqueue(new DragSample(time, new PointF(x, y)));
      while (dragHistory.Count > 2 && time - dragHistory.Peek().Time > 0.18) dragHistory.Dequeue();
      dragSampleTime = time; dragSamplePoint = new PointF(x, y);
    }
    private bool ReleaseVelocity() {
      releaseVX = releaseVY = 0;
      if (Now - dragSampleTime > 0.10 || dragHistory.Count < 3) return false;
      DragSample[] samples = dragHistory.ToArray();
      DragSample last = samples[samples.Length - 1], pivot = samples[0];
      foreach (DragSample sample in samples) if (sample.Time <= last.Time - 0.06) pivot = sample;
      double span = last.Time - pivot.Time; if (span < 0.008) return false;
      double vx = (last.At.X - pivot.At.X) / span, vy = (last.At.Y - pivot.At.Y) / span;
      double speed = Math.Sqrt(vx * vx + vy * vy);
      double olderSpan = pivot.Time - samples[0].Time;
      if (olderSpan >= 0.02) {
        double oldX = (pivot.At.X - samples[0].At.X) / olderSpan, oldY = (pivot.At.Y - samples[0].At.Y) / olderSpan;
        double oldSpeed = Math.Sqrt(oldX * oldX + oldY * oldY);
        if (speed < oldSpeed * 0.8) return false; // Clear braking trend: treat as a deliberate placement.
      }
      if (speed < 450) return false;
      double factor = speed > 1800 ? 1800 / speed : 1;
      releaseVX = vx * factor; releaseVY = vy * factor; return true;
    }
    private Rectangle flightArea;
    private static double Now { get { return (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency; } }
    internal void CancelFlight() { airborne = false; velocityX = velocityY = releaseVX = releaseVY = 0; }
    internal void SetAvoided(bool hide) {
      if (avoided == hide || IsDisposed) return;
      avoided = hide;
      if (hide) { revealPending = false; CancelTransient(false); app.StopAudio(this); dragging = false; moved = true; Capture = false; CancelFlight(); Hide(); }
      else {
        // Ordinary hiding did not invalidate the already presented DIB. Keep its
        // key so a cold desired frame retains that image instead of selecting idle.
        // Cancelled visual effects did invalidate their key; render clean pixels
        // before revealing, even if their replacement must finish decoding first.
        revealPending = true; Render();
      }
    }
    public int PetSize { get; private set; }
    internal Rectangle LandingBounds {
      get { return splitting ? new Rectangle((int)(splitX + splitDX), (int)(splitY + splitDY), Width, Height) : Bounds; }
    }
    internal PetWindow(PetContext context, PetRole initialRole) {
      app = context; Role = initialRole;
      try {
      PetSize = 180; nextBlink = 4 + app.Random.NextDouble() * 4;
      FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
      StartPosition = FormStartPosition.Manual;
      SetStyle(ControlStyles.StandardDoubleClick, true);
      menu = new ContextMenuStrip();
      ToolStripMenuItem rolesMenu = new ToolStripMenuItem("切换这一只（仅本次会话）");
      Dictionary<PetRole, ToolStripMenuItem> roleItems = new Dictionary<PetRole, ToolStripMenuItem>();
      foreach (PetRole item in app.Catalog.Roles) {
        PetRole selected = item;
        ToolStripMenuItem choice = new ToolStripMenuItem(selected.Name);
        choice.Click += delegate { SwitchRole(selected); if (Role == selected) app.RememberRole(selected); };
        rolesMenu.DropDownItems.Add(choice); roleItems.Add(selected, choice);
      }
      menu.Items.Add(rolesMenu);
      menu.Opening += delegate { foreach (KeyValuePair<PetRole, ToolStripMenuItem> item in roleItems) item.Value.Checked = item.Key == Role; };
      ToolStripMenuItem actionItem = new ToolStripMenuItem("本角色动作（单击）", null, delegate { StartLaugh(); });
      menu.Items.Add(actionItem);
      menu.Opening += delegate { bool available = Role.Laugh.Paths.Length > 0; actionItem.Enabled = available && !app.Paused; actionItem.Text = available ? Role.ActionName + (Role.Id == "demo-shapes" ? "（点嘴巴）" : "（单击）") : "此角色仅有几何静态占位图"; };
      ToolStripItem recoveryAction = menu.Items.Add("停止动作 / 自动站回（程序过渡）", null, delegate { BeginRecovery(); });
      ToolStripItem splitAction = menu.Items.Add("分裂一只（最多6只）", null, delegate { app.Spawn(this); });
      menu.Opening += delegate { recoveryAction.Enabled = splitAction.Enabled = !app.Paused; };
      menu.Items.Add("集合到这一屏", null, delegate { app.Gather(Screen.FromControl(this).WorkingArea); });
      menu.Items.Add("添加几何占位角色（补齐角色，最多6只）", null, delegate { app.SummonMissing(Screen.FromControl(this).WorkingArea); });
      menu.Items.Add("只留下这一只", null, delegate { app.KeepOnly(this); });
      ToolStripMenuItem throwing = new ToolStripMenuItem("快速甩出后弹跳（可关闭）");
      throwing.Click += delegate { app.ToggleThrow(); throwing.Checked = app.ThrowEnabled; };
      menu.Items.Add(throwing);
      ToolStripMenuItem fullscreen = new ToolStripMenuItem("全屏自动避让（恢复本轮静音，默认关）");
      fullscreen.Click += delegate { app.ToggleFullscreen(); fullscreen.Checked = app.FullscreenAvoidance; };
      menu.Items.Add(fullscreen);
      menu.Items.Add("全部暂停 / 继续（本轮声音不续播）", null, delegate { app.TogglePause(); });
      ToolStripMenuItem soundItem = new ToolStripMenuItem("动作声音（本演示没有音频）");
      soundItem.Click += delegate { app.ToggleSound(); soundItem.Checked = app.SoundEnabled; };
      menu.Items.Add(soundItem);
      ToolStripMenuItem autoStand = new ToolStripMenuItem("笑倒0.7秒自动站回（首次默认开）");
      autoStand.Click += delegate { app.ToggleAutoStand(); autoStand.Checked = app.AutoStand; };
      menu.Items.Add(autoStand);
      ToolStripMenuItem recovery = new ToolStripMenuItem("站回用淡出淡入（程序过渡）");
      recovery.Click += delegate { app.ToggleRecovery(); recovery.Checked = app.RecoveryTransition; };
      menu.Items.Add(recovery);
      app.AddHoverMenu(menu);
      menu.Items.Add("几何占位：点圆点动作 / 点椭圆弹动 / 拖动抓取", null, delegate { app.Notice("分区交互", "站立空闲时点嘴笑、点肚弹；普通身体可抓取，也可双击分裂。播放或站回时仍可拖动，拖后松手不补点。默认悬停关闭；暂停时动作停用。肚子是程序形变，其他占位角色保留单击动作。"); });
      menu.Opening += delegate { soundItem.Checked = app.SoundEnabled; autoStand.Checked = app.AutoStand; recovery.Checked = app.RecoveryTransition; throwing.Checked = app.ThrowEnabled; fullscreen.Checked = app.FullscreenAvoidance; };
      ToolStripMenuItem size = new ToolStripMenuItem("大小");
      size.DropDownItems.Add("小 · 120px", null, delegate { ResizePet(120); });
      size.DropDownItems.Add("中 · 180px", null, delegate { ResizePet(180); });
      size.DropDownItems.Add("大 · 260px", null, delegate { ResizePet(260); });
      menu.Items.Add(size);
      menu.Items.Add("收回这一只", null, delegate { app.Remove(this); });
      menu.Items.Add("全部退出", null, delegate { app.Stop(); });
      MouseDown += OnDown;
      MouseMove += OnMove;
      MouseLeave += delegate { hoverInteraction.Observe(false, HoverAllowed, app.HoverPreferences); };
      MouseUp += OnUp;
      MouseCaptureChanged += delegate { if (!Capture && dragging) { gesture.Cancel(); dragging = false; moved = true; CancelFlight(); } };
      Shown += delegate { Render(); };
      FormClosed += delegate { app.WindowClosed(this); };
      Role.Users++; roleRegistered = true; app.Catalog.RequestIdle(Role);
      } catch { Dispose(); throw; }
    }
    protected override void Dispose(bool disposing) {
      if (disposing && !resourcesReleased) {
        resourcesReleased = true;
        try {
          app.StopAudio(this);
          if (roleRegistered) { roleRegistered = false; app.Catalog.Release(Role); }
          if (app.Pets.Contains(this)) app.WindowClosed(this);
        } finally {
          try { if (surface != null) { surface.Dispose(); surface = null; } }
          finally { if (menu != null) menu.Dispose(); }
        }
      }
      base.Dispose(disposing);
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
      get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000; return cp; }
    }
    internal void StartAt(Point location, int size) {
      CancelTransient(false);
      lastRenderKey = null; splitting = false; CancelFlight();
      if (surface != null && (surface.Width != size + 40 || surface.Height != size + 40)) { surface.Dispose(); surface = null; }
      PetSize = size; Width = Height = size + 40;
      x = location.X; y = location.Y; Clamp(); Location = new Point((int)x, (int)y);
      if (IsHandleCreated) Render();
    }
    private void ResizePet(int size) { StartAt(new Point((int)x, (int)y), size); }
    internal void BeginSplit(PetWindow source, Point landing) {
      x = source.Left; y = source.Top; Clamp();
      splitX = x; splitY = y;
      splitDX = landing.X - splitX;
      splitDY = landing.Y - splitY;
      splitElapsed = 0; splitting = true;
      Location = new Point((int)x, (int)y); StartLaugh();
    }
    internal void SwitchRole(PetRole next) {
      CancelTransient(false);
      if (next == Role) return;
      try { app.Catalog.PrepareIdle(next); }
      catch (Exception ex) { app.Notice("角色无法切换", next.Name + "：" + ex.Message); return; }
      app.StopAudio(this); CancelFlight(); splitting = false; dragging = false; moved = true; Capture = false;
      playing = resting = actionPending = false; laughElapsed = restElapsed = 0; activeTime = 0; nextBlink = 4 + app.Random.NextDouble() * 4; blinkStarted = -100;
      PetRole old = Role; Role = next; Role.Users++; app.Catalog.Release(old); app.Catalog.RequestIdle(Role);
      lastRenderKey = null; Render();
    }
    internal void StartLaugh() { if (playing || app.Paused || avoided) return;
      if (Role.Laugh.Paths.Length == 0) return;
      actionAudio.Begin(app.SoundEnabled && !app.Paused && !avoided);
      if (effects.Recovering) resetOpacity = true; effects.Cancel();
      CancelFlight(); playing = actionPending = true; resting = false; laughElapsed = 0; restElapsed = 0; app.Catalog.Prefetch(Role, Role.Laugh, 0); Render(); }
    internal void StopLaugh() { if (effects.Recovering) resetOpacity = true; effects.Cancel(); app.StopAudio(this); playing = actionPending = false; resting = false; laughElapsed = 0; restElapsed = 0; Render(); }
    private void BeginRecovery() {
      if (effects.Recovering || app.Paused || avoided) return;
      app.StopAudio(this); gesture.Cancel();
      if (!app.RecoveryTransition || (!playing && !resting)) { StopLaugh(); return; }
      playing = actionPending = false; resting = true; effects.Recover();
      app.Catalog.RequestIdle(Role); Render();
    }
    private HitTarget HitAt(Point at) {
      if (surface == null || displayedRoleId != Role.Id) return HitTarget.None;
      double u, v; SpriteLayout layout = SpriteLayout.Fit(Width, Height, PetSize, Role.Width, Role.Height);
      if (!layout.Normalize(at.X + .5, at.Y + .5, out u, out v)) return HitTarget.None;
      if (effects.Recovering) return surface.AlphaAt(at.X, at.Y) > 32 ? HitTarget.Busy : HitTarget.None;
      u = BodyZones.UndoElastic(u, v, Role.Height, renderedElastic);
      return BodyZones.Hit(Role.Id, u, v, surface.AlphaAt(at.X, at.Y), displayedStanding && !playing && !resting && !splitting && !airborne, resting);
    }
    private void TouchBelly() { if (!playing && !resting && !effects.Recovering && !app.Paused) { effects.Bounce(); hoverInteraction.RecordTouch(); Render(); } }
    private void OnDown(object sender, MouseEventArgs e) {
      if (e.Button != MouseButtons.Left) return;
      SuppressHover();
      HitTarget target = HitAt(e.Location);
      if (target == HitTarget.None) { gesture.Cancel(); return; }
      gesture.Begin(target, e.Clicks > 1);
      CancelFlight(); dragSampleTime = Now; dragSamplePoint = new PointF(x, y);
      dragHistory.Clear(); dragHistory.Enqueue(new DragSample(dragSampleTime, dragSamplePoint));
      dragging = true; moved = false; dragOffset = e.Location; downAt = Cursor.Position; Capture = true;
    }
    private void OnMove(object sender, MouseEventArgs e) {
      if (!dragging) {
        bool allowed = HoverAllowed && app.HoverPreferences.Enabled;
        if (hoverInteraction.Observe(allowed && HitAt(e.Location) == HitTarget.Belly, allowed, app.HoverPreferences)) TouchBelly();
        return;
      }
      Point cursor = Cursor.Position;
      if (!moved) {
        Size threshold = SystemInformation.DragSize;
        Rectangle clickArea = new Rectangle(downAt.X - threshold.Width / 2, downAt.Y - threshold.Height / 2, threshold.Width, threshold.Height);
        if (clickArea.Contains(cursor)) return;
        moved = true; gesture.Cancel(); splitting = false;
        if (!app.Paused) StopLaugh();
      }
      x = cursor.X - dragOffset.X; y = cursor.Y - dragOffset.Y;
      Clamp();
      SampleDrag(Now);
      Location = new Point((int)x, (int)y);
      if (app.Paused) { if (surface != null) surface.Present(this); } else Render();
    }
    private void OnUp(object sender, MouseEventArgs e) {
      if (e.Button == MouseButtons.Right) { menu.Show(Cursor.Position); return; }
      if (e.Button != MouseButtons.Left || !dragging) return;
      dragging = false; Capture = false; Clamp(); Location = new Point((int)x, (int)y);
      if (moved && app.ThrowEnabled && !app.Paused && ReleaseVelocity()) {
        flightArea = Screen.FromControl(this).WorkingArea;
        velocityX = releaseVX; velocityY = releaseVY; airborne = true;
      }
      PetAction action = gesture.End(HitAt(e.Location), moved || app.Paused);
      if (action == PetAction.Split) { app.Spawn(this); return; }
      if (action == PetAction.Bounce) TouchBelly();
      else if (action == PetAction.Restore || (action == PetAction.Laugh && resting)) BeginRecovery();
      else if (action == PetAction.Laugh) StartLaugh();
    }
    private void Clamp() {
      Rectangle area = Screen.FromPoint(new Point((int)x + Width / 2, (int)y + Height / 2)).WorkingArea;
      x = Math.Max(area.Left, Math.Min(x, area.Right - Width));
      y = Math.Max(area.Top, Math.Min(y, area.Bottom - Height));
    }
    private void AdvanceFlight(double dt) {
      // Display changes are re-resolved by RefreshDisplayBounds every 0.5s, not once per animation tick.
      double remaining = Math.Min(0.05, Math.Max(0, dt));
      while (airborne && remaining > 0) {
        double h = Math.Min(1.0 / 120, remaining); remaining -= h;
        velocityY = Math.Min(1800, velocityY + 1800 * h);
        x += (float)(velocityX * h); y += (float)(velocityY * h);
        float right = Math.Max(flightArea.Left, flightArea.Right - Width), bottom = Math.Max(flightArea.Top, flightArea.Bottom - Height);
        if (x < flightArea.Left) { x = flightArea.Left; velocityX = Math.Abs(velocityX) * 0.55; }
        if (x > right) { x = right; velocityX = -Math.Abs(velocityX) * 0.55; }
        if (y < flightArea.Top) { y = flightArea.Top; velocityY = Math.Abs(velocityY) * 0.45; }
        if (y >= bottom) {
          y = bottom; velocityY = Math.Abs(velocityY) < 110 ? 0 : -Math.Abs(velocityY) * 0.48;
          velocityX *= Math.Exp(-5 * h);
          if (Math.Abs(velocityX) < 20 && Math.Abs(velocityY) < 30) CancelFlight();
        }
      }
    }
    internal void Step(bool paused, double dt) {
      if (IsDisposed || !IsHandleCreated || avoided || (dragging && moved)) return;
      // An invalidated hidden visual may become ready while globally paused.
      // Completing that redraw must not advance any animation/gesture clock.
      if (paused) { if (revealPending) Render(); return; }
      activeTime += dt;
      if (hoverInteraction.Poll(HoverAllowed, app.HoverPreferences)) TouchBelly();
      bool wasRecovering = effects.Recovering;
      // Keep an existing deformation steady during a click press. Do not snap it
      // away on MouseDown or move its zone boundary underneath MouseUp.
      if (!(dragging && !moved && effects.Bouncing)) effects.Advance(dt);
      if (wasRecovering && !effects.Recovering) { resting = playing = actionPending = false; laughElapsed = restElapsed = 0; }
      bool positionChanged = splitting || airborne;
      bool clampSplit = splitting;
      if (splitting) {
        splitElapsed += dt;
        double t = Math.Min(1, splitElapsed / 0.65);
        double ease = 1 - Math.Pow(1 - t, 3);
        x = splitX + (float)(splitDX * ease);
        y = splitY + (float)(splitDY * ease - Math.Sin(t * Math.PI) * 22);
        if (t >= 1) splitting = false;
      }
      else if (airborne) AdvanceFlight(dt);
      if (playing && !actionPending) {
        laughElapsed += dt;
        if (laughElapsed >= Role.Duration) { gesture.Cancel(); app.StopAudio(this); playing = false; resting = Role.HoldLast; restElapsed = 0; }
      }
      else if (resting && !effects.Recovering) { restElapsed += dt; if (app.AutoStand && restElapsed >= .7) BeginRecovery(); }
      if (!playing && !resting && activeTime >= nextBlink) {
        blinkStarted = activeTime; nextBlink = activeTime + 4 + app.Random.NextDouble() * 4;
      }
      if (positionChanged) { if (clampSplit) Clamp(); MoveToCurrentPosition(); }
      Render();
    }
    private void MoveToCurrentPosition() { Point next = new Point((int)x, (int)y); if (Location != next) Location = next; }
    internal void RefreshDisplayBounds() {
      if (IsDisposed || !IsHandleCreated) return;
      if (airborne) flightArea = Screen.FromPoint(new Point(flightArea.Left + flightArea.Width / 2, flightArea.Top + flightArea.Height / 2)).WorkingArea;
      Clamp(); MoveToCurrentPosition();
    }
    private void Render() {
      try {
        RenderCore();
        if (revealPending && !avoided && !IsDisposed && surface != null && lastRenderKey != null) {
          revealPending = false; Show();
        }
      }
      catch (Exception ex) {
        PetRole fallback = app.Catalog.FindFallback(Role);
        app.Notice("角色绘制失败", Role.Name + "：" + ex.Message + (fallback == null ? "；没有可用回退，已收回此桌宠。" : "；已回退到" + fallback.Name));
        if (fallback == null) { app.Remove(this); return; }
        SwitchRole(fallback);
      }
    }
    private void RenderCore() {
      if (!IsHandleCreated || IsDisposed) return;
      // Pause/hide/resize can interrupt a fade while its replacement idle is still
      // loading. Restore visibility immediately; pose-aware hit testing stays guarded.
      if (resetOpacity && surface != null) { surface.Present(this, 255); resetOpacity = false; }
      // Fade exactly the last presented pose, including when manually stopping in
      // mid-action; do not jump to the final lying frame before fading out.
      if (effects.Phase == 1 && surface != null && lastRenderKey != null) { surface.Present(this, effects.Opacity); return; }
      string key, path; RoleFrames sequence = null; int frame = -1;
      if (effects.ShowIdle) { path = Role.Idle; key = path; }
      else if (playing || resting) {
        frame = playing ? Math.Min(Role.Laugh.Paths.Length - 1, (int)(laughElapsed * Role.Laugh.Fps)) : Role.Laugh.Paths.Length - 1;
        path = Role.Laugh.Paths[frame]; key = Role.Id + ":laugh:" + frame; sequence = Role.Laugh;
      }
      else {
        Point mouse = Cursor.Position;
        bool hover = Bounds.Contains(mouse);
        int count = Role.Look.Paths.Length;
        int look = count == 0 ? 0 : (hover ? Math.Max(0, Math.Min(count - 1, (int)Math.Round((mouse.X - Left) * (count - 1.0) / Math.Max(1, Width)))) : 0);
        RoleFrames blink = look > count / 2 ? Role.BlinkRight : Role.BlinkLeft;
        double blinkTime = activeTime - blinkStarted;
        bool blinking = blink.Paths.Length > 0 && blinkTime >= 0 && blinkTime < blink.Paths.Length / blink.Fps;
        frame = blinking ? Math.Min(blink.Paths.Length - 1, (int)(blinkTime * blink.Fps)) : -1;
        path = blinking ? blink.Paths[frame] : (count > 0 ? Role.Look.Paths[look] : Role.Idle);
        sequence = blinking ? blink : null; key = path;
      }
      bool actionFrame = playing || (resting && !effects.ShowIdle);
      bool standingFrame = (!playing && !resting) || effects.ShowIdle;
      key += ":effect:" + effects.Stamp;
      if (key == lastRenderKey) { if (playing && actionPending && actionFrame) { actionPending = false; if (actionAudio.TryConsume()) app.PlayAudio(this); } return; }
      RoleCatalog.FrameLease lease = app.Catalog.TryAcquire(Role, path);
      try { if (sequence != null) app.Catalog.Prefetch(Role, sequence, frame + 1); }
      catch { if (lease != null) lease.Dispose(); throw; }
      if (lease == null) {
        // Keep the last valid visual while the worker decodes; never block mouse/timer messages.
        if (surface != null && lastRenderKey != null) { surface.Present(this, effects.Opacity); return; }
        lease = app.Catalog.TryAcquire(Role, Role.Idle); key = Role.Idle + ":effect:" + effects.Stamp; actionFrame = false; standingFrame = true;
        if (lease == null) return;
      }
      long begin = Stopwatch.GetTimestamp();
      using (lease) {
        if (surface == null) surface = new LayeredSurface(Width, Height);
        renderedElastic = Role.Id == "demo-shapes" ? effects.Elastic : 0;
        surface.Draw(lease.Image, PetSize, renderedElastic); surface.Present(this, effects.Opacity); lastRenderKey = key;
        displayedStanding = standingFrame; displayedRoleId = Role.Id;
      }
      if (effects.ShowIdle) effects.IdlePresented();
      PerformanceProbe.RecordRender((Stopwatch.GetTimestamp() - begin) * 1000.0 / Stopwatch.Frequency);
      if (playing && actionPending && actionFrame) { actionPending = false; if (actionAudio.TryConsume()) app.PlayAudio(this); }
    }
  }
  internal static class Native {
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    internal static Rectangle? ForegroundFullscreen(List<PetWindow> pets) {
      IntPtr hwnd = GetForegroundWindow();
      if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return null;
      foreach (PetWindow pet in pets) if (!pet.IsDisposed && pet.IsHandleCreated && hwnd == pet.Handle) return null;
      StringBuilder className = new StringBuilder(128); GetClassName(hwnd, className, className.Capacity);
      string kind = className.ToString();
      if (kind == "Progman" || kind == "WorkerW" || kind == "Shell_TrayWnd" || kind == "Shell_SecondaryTrayWnd") return null;
      RECT rect; if (!GetWindowRect(hwnd, out rect)) return null;
      Rectangle monitor = Screen.FromHandle(hwnd).Bounds;
      // Conservative geometric heuristic; no foreign window activation, resize or minimization.
      if (Math.Abs((long)rect.left - monitor.Left) <= 2 && Math.Abs((long)rect.top - monitor.Top) <= 2 &&
          Math.Abs((long)rect.right - monitor.Right) <= 2 && Math.Abs((long)rect.bottom - monitor.Bottom) <= 2) return monitor;
      return null;
    }
  }
}
