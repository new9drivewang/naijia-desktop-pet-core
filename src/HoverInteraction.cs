using System;

namespace NaiPet {
  internal sealed class HoverPreferences {
    internal readonly bool Enabled;
    internal readonly int CooldownMilliseconds;
    internal HoverPreferences() : this(false, 800) { }
    internal HoverPreferences(bool enabled, int cooldownMilliseconds) {
      if (cooldownMilliseconds != 800 && cooldownMilliseconds != 2000 && cooldownMilliseconds != 5000)
        throw new ArgumentOutOfRangeException("cooldownMilliseconds");
      Enabled = enabled; CooldownMilliseconds = cooldownMilliseconds;
    }
  }

  // Pointer events own entry/exit. The existing UI timer only polls a monotonic
  // dwell deadline; it never queries the cursor, starts a timer or queues work.
  internal sealed class HoverInteraction {
    internal const double DwellSeconds = .4;
    private readonly Func<double> clock;
    private bool inside, requireExit, consumed;
    private double entered;
    private double lastTouch = Double.NegativeInfinity;
    internal HoverInteraction(Func<double> clock) {
      if (clock == null) throw new ArgumentNullException("clock");
      this.clock = clock;
    }
    internal void Suppress() { requireExit = true; inside = false; consumed = true; }
    internal void RecordTouch() { lastTouch = clock(); inside = consumed = true; }
    internal bool Observe(bool belly, bool allowed, HoverPreferences preferences) {
      if (!allowed || !preferences.Enabled) { Suppress(); return false; }
      if (!belly) { inside = requireExit = consumed = false; return false; }
      if (requireExit) return false;
      if (!inside) {
        inside = true; entered = clock();
        // A cooldown-rejected entry is consumed; waiting cannot turn it into a touch.
        consumed = entered < lastTouch + preferences.CooldownMilliseconds / 1000.0;
      }
      return Poll(allowed, preferences);
    }
    internal bool Poll(bool allowed, HoverPreferences preferences) {
      if (!allowed || !preferences.Enabled) { Suppress(); return false; }
      if (!inside || requireExit || consumed || clock() < entered + DwellSeconds) return false;
      consumed = true; lastTouch = clock(); return true;
    }
  }

  // Public demo preferences exist only in the current session.
  internal static class HoverPreferenceStore {
    internal static HoverPreferences Load(string directory, out string warning) {
      warning = null; return new HoverPreferences();
    }
    internal static void Save(string directory, HoverPreferences preferences) { }
  }
}
