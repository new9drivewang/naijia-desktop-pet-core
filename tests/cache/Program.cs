using System;
using System.Diagnostics;
using System.Threading;
using NaiPet;
using System.Drawing;
class Program {
 static void Assert(bool value,string label) {if(!value) throw new Exception(label);}
 static RoleCatalog.FrameLease Await(RoleCatalog c,PetRole r,string p) {
  Stopwatch s=Stopwatch.StartNew(); while(s.ElapsedMilliseconds<5000) {var l=c.TryAcquire(r,p);if(l!=null)return l;Thread.Sleep(1);}throw new TimeoutException(p);
 }
 static void ColdResumeFixtureEvictsAndBlocksAction() {
  var c=new RoleCatalog();var visible=new PetRole();var pressure=new PetRole{Id="pressure",Idle="pressure-idle"};
  const string current="presented-action";
  using(var entered=new ManualResetEvent(false))using(var proceed=new ManualResetEvent(false)) {
   try {
    using(var frame=Await(c,visible,current)) {Assert(frame.Image.Width==16,"action prepared");}
    for(int i=0;i<RoleCatalog.CacheEntryLimit+RoleCatalog.LookAhead+1;i++)using(var frame=Await(c,pressure,"pressure-"+i)) {}
    Assert(!c.Contains(current),"fixture failed to evict current action");
    c.PrepareIdle(visible);
    c.BeforeDecode=(role,path)=>{if(role==visible&&path==current){entered.Set();Assert(proceed.WaitOne(5000),"decode latch timeout");}};
    c.Request(visible,current);Assert(entered.WaitOne(5000),"current-frame decode was not blocked");
    using(var missing=c.TryAcquire(visible,current)) {Assert(missing==null,"cold action unexpectedly ready");}
    using(var idle=c.TryAcquire(visible,visible.Idle)) {Assert(idle!=null,"idle fallback was not available");}
    Assert(!c.Contains(current),"blocked decode published action");
    proceed.Set();using(var frame=Await(c,visible,current)) {Assert(frame!=null,"action did not recover after latch release");}
   } finally {proceed.Set();c.Finish();}
  }
 }
 static void Lifecycle() {
  var c=new RoleCatalog();var r=new PetRole();r.Users=2;
  try {
   c.PrepareIdle(r);var pinned=c.TryAcquire(r,r.Idle);
   for(int i=0;i<84;i++) using(var l=Await(c,r,"frame"+i)) {Assert(l.Image.Width==16,"read");}
   Assert(c.Count==60&&!c.Contains("frame0"),"eviction");Assert(pinned.Image.Width==16,"pin survives eviction");
   c.Release(r);Assert(c.Contains(r.Idle),"shared role retained");c.Release(r);
   Assert(c.Count==1&&!c.Contains(r.Idle),"retired lease counted");Assert(pinned.Image.Width==16,"pin survives release");
   pinned.Dispose();pinned.Dispose();Assert(c.Count==0,"lease cleanup");
  }finally {c.Finish();}
 }
 static void QueueAndGeneration() {
  var c=new RoleCatalog();var r=new PetRole();using(var entered=new ManualResetEvent(false))using(var proceed=new ManualResetEvent(false)) {
   c.BeforeDecode=(role,path)=>{if(path=="stale"){entered.Set();Assert(proceed.WaitOne(5000),"gate timeout");}};
   try {
    c.Request(r,"stale");Assert(entered.WaitOne(5000),"worker gate");
    for(int i=0;i<200;i++) c.Request(r,"frame"+i);Assert(c.Queued==64,"queue cap");
    c.Release(r);Assert(c.Queued==0,"release cancellation");r.Users=1;proceed.Set();
    using(var l=Await(c,r,"sentinel")) {} Assert(!c.Contains("stale"),"stale result not published");
   }finally {proceed.Set();c.Finish();}
  }
 }
 static void Shutdown() {
  var c=new RoleCatalog();var r=new PetRole();using(var entered=new ManualResetEvent(false))using(var proceed=new ManualResetEvent(false)) {
   c.BeforeDecode=(role,path)=>{entered.Set();Assert(proceed.WaitOne(5000),"gate timeout");};
   try {c.Request(r,"blocked");Assert(entered.WaitOne(5000),"worker gate");c.Dispose();c.Dispose();Assert(c.TryAcquire(r,r.Idle)==null,"post-dispose acquire");}
   finally{proceed.Set();c.Finish();} Assert(c.Count==0,"shutdown cleanup");
  }
 }
 static void LateFailureMustNotPoisonReadyIdle() {
  var c=new RoleCatalog();var r=new PetRole();int ui=Thread.CurrentThread.ManagedThreadId;
  using(var entered=new ManualResetEvent(false))using(var proceed=new ManualResetEvent(false)) {
   c.BeforeDecode=(role,path)=>{if(path==r.Idle){entered.Set();Assert(proceed.WaitOne(5000),"gate timeout");}};
   Image.Loader=p=>{if(p==r.Idle&&Thread.CurrentThread.ManagedThreadId!=ui)throw new System.IO.IOException("injected worker read failure");return new Image();};
   try {
    c.Request(r,r.Idle);Assert(entered.WaitOne(5000),"worker gate");c.PrepareIdle(r);proceed.Set();
    using(var sentinel=Await(c,r,"sentinel")) {} // Single worker guarantees failure is recorded first.
    using(var idle=c.TryAcquire(r,r.Idle)) {Assert(idle!=null&&idle.Image.Width==16,"successfully prepared idle remains usable");}
   }finally {proceed.Set();c.Finish();Image.Loader=p=>new Image();}
  }
 }
 static void PrepareIdleRecoversEarlierFailure() {
  var c=new RoleCatalog();var r=new PetRole();int ui=Thread.CurrentThread.ManagedThreadId;
  Image.Loader=p=>{if(p==r.Idle&&Thread.CurrentThread.ManagedThreadId!=ui)throw new System.IO.IOException("earlier read failure");return new Image();};
  try {
   c.Request(r,r.Idle);using(var sentinel=Await(c,r,"sentinel")) {}
   // Ensure idle was processed: requests for sentinel can take urgent priority.
   Stopwatch watch=Stopwatch.StartNew();bool failed=false;
   while(watch.ElapsedMilliseconds<5000&&!failed) {try {using(var ignored=c.TryAcquire(r,r.Idle)) {}}catch(System.IO.InvalidDataException){failed=true;}Thread.Sleep(1);}
   Assert(failed,"initial failure was observed");c.PrepareIdle(r);
   using(var idle=c.TryAcquire(r,r.Idle)) {Assert(idle!=null&&idle.Image.Width==16,"successful retry clears earlier failure");}
  } finally {c.Finish();Image.Loader=p=>new Image();}
 }
 static int Main() {
  Console.WriteLine("Real FrameCache.cs logic; Bitmap/I/O doubles. NOT Windows, PNG or GDI tests.");
  int failed=0;foreach(Action test in new Action[]{Lifecycle,QueueAndGeneration,Shutdown,LateFailureMustNotPoisonReadyIdle,PrepareIdleRecoversEarlierFailure,UrgentFifo,SpeculationCannotEvictUrgent,InFlightDedupAndNewGeneration,IdleWarmsOriginalAction,UrgentDisplacesSpeculation,LargeFramePrefetchBudget,ClearRolePixelBudget,ColdResumeFixtureEvictsAndBlocksAction}) {
   try{test();Console.WriteLine("PASS "+test.Method.Name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+test.Method.Name+": "+e);}
  }Console.WriteLine("RESULT: 13 cache scenarios, " + failed + " failures; image/I/O doubles only."); return failed==0?0:1;
 }
 static void Blocked(Action<RoleCatalog,PetRole> check) {
  var c=new RoleCatalog();var r=new PetRole();using(var entered=new ManualResetEvent(false))using(var proceed=new ManualResetEvent(false)) {
   c.BeforeDecode=(role,path)=>{if(path=="blocked"){entered.Set();Assert(proceed.WaitOne(5000),"gate timeout");}};
   try {c.Request(r,"blocked");Assert(entered.WaitOne(5000),"worker gate");check(c,r);}
   finally {c.Dispose();proceed.Set();c.Finish();}
  }
 }
 static void UrgentFifo() {Blocked((c,r)=>{
  c.Request(r,"promoted");c.TryAcquire(r,"pet1");c.TryAcquire(r,"promoted");c.TryAcquire(r,"pet2");
  Assert(String.Join(",",c.QueuePaths)=="pet1,promoted,pet2","urgent FIFO/promotion order: "+String.Join(",",c.QueuePaths));
 });}
 static void SpeculationCannotEvictUrgent() {Blocked((c,r)=>{
  for(int i=0;i<64;i++)c.TryAcquire(r,"urgent"+i);
  string before=String.Join(",",c.QueuePaths);c.Request(r,"speculative");
  Assert(String.Join(",",c.QueuePaths)==before,"speculation displaced urgent work");
  c.TryAcquire(r,"overflow");Assert(String.Join(",",c.QueuePaths)==before,"full urgent queue must remain bounded and stable");
 });}
 static void InFlightDedupAndNewGeneration() {Blocked((c,r)=>{
  for(int i=0;i<100;i++)c.TryAcquire(r,"blocked");Assert(c.Queued==0,"in-flight request duplicated");
  c.Release(r);r.Users=1;c.TryAcquire(r,"blocked");Assert(c.Queued==1,"new generation request suppressed by old in-flight request");
 });}
 static void IdleWarmsOriginalAction() {Blocked((c,r)=>{
  r.Laugh.Paths=new[]{"laugh0","laugh1","laugh2","laugh3","laugh4","laugh5"};c.RequestIdle(r);
  Assert(String.Join(",",c.QueuePaths)=="idle,laugh0,laugh1,laugh2,laugh3","only first four original frames are warmed");
 });}
 static void UrgentDisplacesSpeculation() {Blocked((c,r)=>{
  for(int i=0;i<64;i++)c.Request(r,"spec"+i);c.TryAcquire(r,"visible");
  Assert(c.Queued==64&&c.QueuePaths[0]=="visible"&&Array.IndexOf(c.QueuePaths,"spec63")==-1,"urgent request did not replace speculation");
 });}
 static void LargeFramePrefetchBudget() {Blocked((c,r)=>{
  r.Width=r.Height=1024;c.Roles.Add(r);for(int i=1;i<6;i++)c.Roles.Add(new PetRole());
  c.Prefetch(r,new RoleFrames{Paths=new[]{"large0","large1","large2","large3"}},0);
  Assert(String.Join(",",c.QueuePaths)=="large0","six-pet large-frame speculation must shrink without resizing images");
 });}
 static void ClearRolePixelBudget() {
  var c=new RoleCatalog();var r=new PetRole{Width=540,Height=737};c.Roles.Add(r);
  r.Laugh.Paths=new string[455];for(int i=0;i<455;i++)r.Laugh.Paths[i]="clear"+i;
  using(var entered=new ManualResetEvent(false))using(var proceed=new ManualResetEvent(false)) {
   c.BeforeDecode=(role,path)=>{if(path=="blocked"){entered.Set();Assert(proceed.WaitOne(5000),"gate timeout");}};
   Image.Loader=p=>new Image(540,737);
   try {
    c.PrepareIdle(r);
    for(int i=0;i<35;i++)using(var frame=Await(c,r,r.Laugh.Paths[i])){Assert(frame.Image.Width==540&&frame.Image.Height==737,"clear dimensions");}
    Assert(c.Count==26,"40MiB should hold 26 simulated 540x737 decoded frames");
    c.Request(r,"blocked");Assert(entered.WaitOne(5000),"worker gate");
    r.Users=6;c.Prefetch(r,r.Laugh,100);Assert(c.Queued==2,"six-pet clear-frame lookahead");
    r.Users=1;c.Prefetch(r,r.Laugh,200);Assert(c.Queued==6,"one-pet lookahead is four, not the full sequence");
   }finally{c.Dispose();proceed.Set();c.Finish();Image.Loader=p=>new Image();}
  }
 }
}

