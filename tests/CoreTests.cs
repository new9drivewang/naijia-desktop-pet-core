using System;
using NaiPet;

internal static class CoreTests {
  private static int passed;
  private static void Check(bool value, string name) {
    if (!value) throw new Exception("FAIL " + name);
    passed++; Console.WriteLine("PASS " + name);
  }
  public static void Main() {
    Check(BodyZones.Hit("demo-shapes", .365, .194, 255, true, false) == HitTarget.Mouth, "generated action marker maps to mouth zone");
    Check(BodyZones.Hit("demo-shapes", .44, .475, 255, true, false) == HitTarget.Belly, "generated belly marker maps to belly zone");
    Check(BodyZones.Hit("demo-shapes", .44, .475, 0, true, false) == HitTarget.None, "transparent pixels ignore input");
    Check(BodyZones.Hit("demo-shapes", .44, .475, 255, false, false) == HitTarget.Busy, "busy pose blocks body actions");
    GestureState gesture = new GestureState();
    gesture.Begin(HitTarget.Belly, false);
    Check(gesture.End(HitTarget.Belly, true) == PetAction.None, "drag release does not click");
    gesture.Begin(HitTarget.Mouth, false);
    Check(gesture.End(HitTarget.Mouth, false) == PetAction.Laugh, "action click");
    gesture.Begin(HitTarget.Body, false); gesture.End(HitTarget.Body, false); gesture.Begin(HitTarget.Body, true);
    Check(gesture.End(HitTarget.Body, false) == PetAction.Split, "body double click splits");
    VisualEffects effects = new VisualEffects(); effects.Recover(); effects.Advance(.125);
    Check(effects.Phase == 2 && effects.Opacity == 0, "recovery waits invisible for own idle");
    effects.IdlePresented(); effects.Advance(.125);
    Check(effects.Phase == 0 && effects.Opacity == 255, "presented idle completes recovery");
    effects.Bounce(); effects.Advance(.043);
    Check(effects.Bouncing && effects.Elastic > .7, "bounce peak remains active");
    effects.Cancel(); Check(!effects.Bouncing && effects.Opacity == 255, "pause cancellation clears visual effects");
    ActionAudioGate audio = new ActionAudioGate(); audio.Begin(true); audio.Suppress();
    Check(!audio.TryConsume(), "suppressed action never restarts audio");
    double now = 0; HoverInteraction hover = new HoverInteraction(delegate { return now; });
    HoverPreferences preferences = new HoverPreferences(true, 800);
    hover.Observe(true, true, preferences); now = .39;
    Check(!hover.Poll(true, preferences), "hover requires full dwell"); now = .401;
    Check(hover.Poll(true, preferences) && !hover.Poll(true, preferences), "hover fires once per entry");
    hover.Suppress(); now = 2;
    Check(!hover.Observe(true, true, preferences), "suppression requires pointer exit");
    hover.Observe(false, true, preferences); hover.Observe(true, true, preferences); now = 2.401;
    Check(hover.Poll(true, preferences), "exit and reentry permit fresh dwell");
    string warning;
    PetSettings settings = LocalSettings.Load("unused", out warning);
    Check(!settings.Sound && settings.PreferredRole == "demo-shapes" && warning == null, "offline demo defaults");
    settings.Sound = true; LocalSettings.Save("unused", settings);
    Check(!LocalSettings.Load("unused", out warning).Sound, "preferences do not persist");
    Console.WriteLine("RESULT: " + passed + " core assertions passed; no Windows UI or physical input exercised.");
  }
}
