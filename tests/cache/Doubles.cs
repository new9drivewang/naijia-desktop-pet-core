// Explicit platform doubles: NO PNG decoding, WinForms, or Windows GDI is exercised.
// The linked FrameCache.cs is the actual candidate implementation, not a cache model.
using System;
using System.Threading;
namespace System.Drawing {
 public class Image : IDisposable {
  public static Func<string,Image> Loader = p => new Image();
  protected bool disposed;
  private readonly int width, height;
  public Image(int width=16,int height=16) {this.width=width;this.height=height;}
  public int Width { get { if(disposed) throw new ObjectDisposedException("Image"); return width; } }
  public int Height { get { if(disposed) throw new ObjectDisposedException("Image"); return height; } }
  public static Image FromFile(string p) { return Loader(p); }
  public virtual void Dispose() { if(disposed) throw new InvalidOperationException("double image disposal"); disposed=true; }
 }
 public class Bitmap : Image { public Bitmap(Image source) : base(source.Width,source.Height) {} }
}
namespace NaiPet {
 internal sealed class PetRole { internal string Id="test",Name="test",Idle="idle"; internal int Width=16,Height=16,Users=1; internal RoleFrames Laugh=new RoleFrames(),Look=new RoleFrames(),BlinkLeft=new RoleFrames(),BlinkRight=new RoleFrames(); }
 internal sealed class RoleFrames { internal string[] Paths=new string[0]; }
 internal static class PerformanceProbe {
  internal static void RecordDecode(double x) {} internal static void RecordCache(bool x) {}
  internal static void RecordCacheState(long bytes,int count,int queued) {
   if(bytes<0||bytes>40L*1024*1024||count<0||count>60||queued<0||queued>64) throw new InvalidOperationException("cache bound");
  }
 }
 internal sealed partial class RoleCatalog {
  internal readonly System.Collections.Generic.List<PetRole> Roles = new System.Collections.Generic.List<PetRole>();
  internal readonly System.Collections.Generic.List<string> Warnings = new System.Collections.Generic.List<string>();
  internal PetRole Default { get {return selectedDefault ?? Roles[0];} }
  internal Action<PetRole,string> BeforeDecode;
  private void TestBeforeDecode(PetRole r,string p) { if(BeforeDecode!=null) BeforeDecode(r,p); }
  private static string AssetPath(PetRole r,string p,string e) { return p; }
  private static void CheckHeader(string p,PetRole r) {}
  internal int Count {get {lock(cacheGate) return allocatedEntries;}}
  internal int Queued {get {lock(cacheGate) return pending.Count;}}
  internal string[] QueuePaths { get { lock(cacheGate) { var paths=new System.Collections.Generic.List<string>();foreach(var r in work)paths.Add(r.Path);return paths.ToArray(); } } }
  internal bool Contains(string p) {lock(cacheGate) return images.ContainsKey(p);}
  internal void Request(PetRole r,string p) {lock(cacheGate) Enqueue(r,p,false);}
  internal void Finish() {Dispose(); if(decoder!=null&&!decoder.Join(5000)) throw new Exception("worker failed to exit");}
 }
}

