using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;

namespace NaiPet {
  // All decoding/asset revalidation after initial idle preparation runs on one worker.
  // Only immutable, fully decoded Bitmap instances cross to the UI thread, via leases.
  internal sealed partial class RoleCatalog {
    internal const int CacheEntryLimit = 60, PendingLimit = 64, LookAhead = 4;
    internal const long CacheByteLimit = 40L * 1024 * 1024;
    private readonly object cacheGate = new object();
    private readonly Dictionary<string, FrameEntry> images = new Dictionary<string, FrameEntry>(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<FrameEntry> lru = new LinkedList<FrameEntry>();
    private readonly Dictionary<string, FrameRequest> pending = new Dictionary<string, FrameRequest>(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<FrameRequest> work = new LinkedList<FrameRequest>();
    private readonly Dictionary<string, FrameFailure> failures = new Dictionary<string, FrameFailure>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<PetRole, int> generations = new Dictionary<PetRole, int>();
    private readonly AutoResetEvent ready = new AutoResetEvent(false);
    private Thread decoder;
    private FrameRequest inFlight;
    private bool stopping;
    private long pixelBytes;
    private int allocatedEntries;
    private sealed class FrameFailure { internal PetRole Role; internal Exception Error; }
    internal sealed class FrameEntry {
      internal PetRole Role; internal string Path; internal Bitmap Image; internal long Bytes;
      internal int Readers; internal bool Retired; internal LinkedListNode<FrameEntry> Node;
    }
    private sealed class FrameRequest {
      internal PetRole Role; internal string Path; internal int Generation; internal bool Urgent;
      internal LinkedListNode<FrameRequest> Node;
    }
    internal sealed class FrameLease : IDisposable {
      private RoleCatalog owner; private FrameEntry entry;
      internal Bitmap Image { get { return entry.Image; } }
      internal FrameLease(RoleCatalog owner, FrameEntry entry) { this.owner = owner; this.entry = entry; }
      public void Dispose() { if (owner == null) return; RoleCatalog old = owner; owner = null; old.ReleaseLease(entry); entry = null; }
    }
    private int Generation(PetRole role) { int value; return generations.TryGetValue(role, out value) ? value : 0; }
    private static Bitmap Decode(PetRole role, string path) {
      long begin = Stopwatch.GetTimestamp();
      try {
        path = AssetPath(role, path, ".png"); // Revalidate containment, links, size and PNG header just before disk access.
        CheckHeader(path, role);
        using (Image decoded = Image.FromFile(path)) {
          if (decoded.Width != role.Width || decoded.Height != role.Height) throw new InvalidDataException("解码尺寸不一致");
          return new Bitmap(decoded);
        }
      } finally { PerformanceProbe.RecordDecode((Stopwatch.GetTimestamp() - begin) * 1000.0 / Stopwatch.Frequency); }
    }
    // User-initiated creation/switch may synchronously prepare ONE idle image, never an animation sequence.
    internal void PrepareIdle(PetRole role) {
      lock (cacheGate) { if (stopping) throw new ObjectDisposedException("RoleCatalog"); if (images.ContainsKey(role.Idle)) return; }
      Bitmap decoded = Decode(role, role.Idle);
      lock (cacheGate) {
        if (stopping) { decoded.Dispose(); throw new ObjectDisposedException("RoleCatalog"); }
        if (images.ContainsKey(role.Idle)) decoded.Dispose();
        else Insert(role, role.Idle, decoded);
      }
    }
    internal FrameLease TryAcquire(PetRole role, string path) {
      lock (cacheGate) {
        if (stopping) return null;
        FrameFailure failure;
        if (failures.TryGetValue(path, out failure)) throw new InvalidDataException("角色图片读取失败：" + path, failure.Error);
        FrameEntry entry;
        if (images.TryGetValue(path, out entry)) {
          entry.Readers++; lru.Remove(entry.Node); lru.AddFirst(entry.Node);
          PerformanceProbe.RecordCache(true); return new FrameLease(this, entry);
        }
        PerformanceProbe.RecordCache(false); Enqueue(role, path, true); return null;
      }
    }
    internal void Prefetch(PetRole role, RoleFrames frames, int first) {
      lock (cacheGate) {
        int ahead = ForwardWindow(role);
        for (int i = Math.Max(0, first); i < Math.Min(frames.Paths.Length, first + ahead); i++) Enqueue(role, frames.Paths[i], false);
      }
    }
    private int ForwardWindow(PetRole role) {
      // This is a speculative pixel budget, not a quality reduction or a promise
      // that codecs/process memory fit the cache budget. Keep original resolution.
      long bytes = Math.Max(1L, (long)role.Width * role.Height * 4);
      int users = 0; foreach (PetRole active in Roles) users += Math.Max(0, active.Users);
      long frames = CacheByteLimit / Math.Max(1, users) / bytes;
      // Reserve room for the current and idle frame; still request one next frame
      // when under pressure so playback can make progress through ordinary eviction.
      return (int)Math.Max(1L, Math.Min((long)LookAhead, frames - 2));
    }
    internal void RequestIdle(PetRole role) {
      lock (cacheGate) {
        Enqueue(role, role.Idle, false);
        if (role.Look.Paths.Length > 0) Enqueue(role, role.Look.Paths[0], false);
        Prefetch(role, role.BlinkLeft, 0); Prefetch(role, role.BlinkRight, 0);
        // Warm only the first few original action frames, within the same cache/queue
        // limits. This does not start playback or sound and never waits on the UI.
        Prefetch(role, role.Laugh, 0);
      }
    }
    private void Enqueue(PetRole role, string path, bool urgent) {
      if (stopping || images.ContainsKey(path) || failures.ContainsKey(path)) return;
      if (inFlight != null && inFlight.Role == role && inFlight.Generation == Generation(role) &&
          String.Equals(inFlight.Path, path, StringComparison.OrdinalIgnoreCase)) return;
      FrameRequest request;
      if (pending.TryGetValue(path, out request)) {
        if (urgent && !request.Urgent) { request.Urgent = true; work.Remove(request.Node); request.Node = QueueRequest(request); }
        return;
      }
      if (pending.Count >= PendingLimit) {
        // Speculation must never displace a frame requested for presentation.
        // If all slots are urgent, keep FIFO order; a rejected caller retries next tick.
        if (!urgent || work.Last.Value.Urgent) return;
        FrameRequest old = work.Last.Value; work.RemoveLast(); pending.Remove(old.Path);
      }
      request = new FrameRequest(); request.Role = role; request.Path = path; request.Generation = Generation(role); request.Urgent = urgent;
      request.Node = QueueRequest(request); pending.Add(path, request);
      if (decoder == null) { decoder = new Thread(DecodeLoop); decoder.IsBackground = true; decoder.Name = "NaiPet PNG prefetch"; decoder.Start(); }
      ready.Set(); ReportCache();
    }
    private LinkedListNode<FrameRequest> QueueRequest(FrameRequest request) {
      if (!request.Urgent) return work.AddLast(request);
      // Urgent requests precede speculation, but are FIFO among themselves so a
      // later pet cannot continually jump ahead of an earlier pet's missing frame.
      LinkedListNode<FrameRequest> next = work.First;
      while (next != null && next.Value.Urgent) next = next.Next;
      return next == null ? work.AddLast(request) : work.AddBefore(next, request);
    }
    private void DecodeLoop() {
      try {
        while (true) {
          FrameRequest request = null;
          lock (cacheGate) {
            if (stopping) return;
            if (work.Count > 0) { request = work.First.Value; work.RemoveFirst(); pending.Remove(request.Path); inFlight = request; }
          }
          if (request == null) { ready.WaitOne(); continue; }
          Bitmap decoded = null;
          try {
            lock (cacheGate) { if (stopping || request.Generation != Generation(request.Role) || images.ContainsKey(request.Path)) continue; }
            TestBeforeDecode(request.Role, request.Path); // Inert except deterministic Windows regression tests.
            decoded = Decode(request.Role, request.Path);
            lock (cacheGate) {
              if (!stopping && request.Generation == Generation(request.Role) && !images.ContainsKey(request.Path)) {
                Insert(request.Role, request.Path, decoded); decoded = null;
              }
            }
          } catch (Exception ex) {
            lock (cacheGate) {
              // A synchronous PrepareIdle may have published a valid frame while this
              // worker was decoding. A later failed attempt must not poison it.
              if (!stopping && request.Generation == Generation(request.Role) && !images.ContainsKey(request.Path)) {
                if (failures.Count >= PendingLimit) failures.Clear();
                FrameFailure failure = new FrameFailure(); failure.Role = request.Role; failure.Error = ex; failures[request.Path] = failure;
              }
            }
          } finally {
            try { if (decoded != null) decoded.Dispose(); }
            finally { lock (cacheGate) { inFlight = null; } }
          }
        }
      } finally { ready.Dispose(); }
    }
    private void Insert(PetRole role, string path, Bitmap image) {
      long bytes = (long)image.Width * image.Height * 4;
      while (allocatedEntries >= CacheEntryLimit || pixelBytes + bytes > CacheByteLimit) {
        LinkedListNode<FrameEntry> candidate = lru.Last;
        while (candidate != null && candidate.Value.Readers != 0) candidate = candidate.Previous;
        if (candidate == null) { image.Dispose(); return; } // Never dispose a Bitmap currently used by GDI+ on the UI thread.
        Retire(candidate.Value);
      }
      FrameEntry entry = new FrameEntry(); entry.Role = role; entry.Path = path; entry.Image = image; entry.Bytes = bytes;
      entry.Node = lru.AddFirst(entry); images.Add(path, entry); failures.Remove(path);
      pixelBytes += bytes; allocatedEntries++; ReportCache();
    }
    private void Retire(FrameEntry entry) {
      images.Remove(entry.Path); lru.Remove(entry.Node); entry.Retired = true;
      if (entry.Readers == 0) Destroy(entry);
    }
    private void Destroy(FrameEntry entry) { entry.Image.Dispose(); entry.Image = null; pixelBytes -= entry.Bytes; allocatedEntries--; }
    private void ReleaseLease(FrameEntry entry) {
      lock (cacheGate) { entry.Readers--; if (entry.Readers == 0 && entry.Retired) Destroy(entry); ReportCache(); }
    }
    private void ReportCache() { PerformanceProbe.RecordCacheState(pixelBytes, allocatedEntries, pending.Count); }
    internal void Release(PetRole role) {
      role.Users = Math.Max(0, role.Users - 1); if (role.Users != 0) return;
      lock (cacheGate) {
        generations[role] = Generation(role) + 1; // In-flight old-role results cannot repopulate a released cache.
        LinkedListNode<FrameRequest> request = work.First;
        while (request != null) { LinkedListNode<FrameRequest> next = request.Next; if (request.Value.Role == role) { pending.Remove(request.Value.Path); work.Remove(request); } request = next; }
        LinkedListNode<FrameEntry> node = lru.First;
        while (node != null) { LinkedListNode<FrameEntry> next = node.Next; if (node.Value.Role == role) Retire(node.Value); node = next; }
        List<string> failed = new List<string>(); foreach (KeyValuePair<string, FrameFailure> pair in failures) if (pair.Value.Role == role) failed.Add(pair.Key);
        foreach (string path in failed) failures.Remove(path); ReportCache();
      }
    }
    public void Dispose() {
      lock (cacheGate) {
        if (stopping) return; stopping = true;
        work.Clear(); pending.Clear(); failures.Clear();
        while (lru.Count > 0) Retire(lru.First.Value);
        if (decoder == null) ready.Dispose(); else ready.Set(); // No UI-thread Join; worker owns final event disposal.
        ReportCache();
      }
    }
  }
}
