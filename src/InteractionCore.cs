using System;

namespace NaiPet {
  internal enum HitTarget { None, Body, Mouth, Belly, Resting, Legacy, Busy }
  internal enum PetAction { None, Laugh, Bounce, Split, Restore }
  internal struct SpriteLayout {
    internal double X, Y, Width, Height;
    internal static SpriteLayout Fit(int windowWidth, int windowHeight, int petSize, int imageWidth, int imageHeight) {
      if (windowWidth <= 0 || windowHeight <= 0 || petSize <= 0 || imageWidth <= 0 || imageHeight <= 0) throw new ArgumentOutOfRangeException("imageWidth");
      double scale = Math.Min((double)petSize / imageWidth, (double)petSize / imageHeight);
      SpriteLayout r = new SpriteLayout(); r.Width = imageWidth * scale; r.Height = imageHeight * scale;
      r.X = (windowWidth - r.Width) / 2; r.Y = (windowHeight - r.Height) / 2; return r;
    }
    internal bool Normalize(double x, double y, out double u, out double v) {
      u = (x - X) / Width; v = (y - Y) / Height;
      return u >= 0 && u < 1 && v >= 0 && v < 1;
    }
  }
  internal static class BodyZones {
    // Normalized zones retained from v7.6; generated demo markers align with these coordinates.
    internal static bool Ellipse(double u, double v, double cx, double cy, double rx, double ry) {
      double x = (u - cx) / rx, y = (v - cy) / ry; return x * x + y * y <= 1;
    }
    internal static HitTarget Hit(string roleId, double u, double v, int alpha, bool standing, bool resting) {
      if (alpha <= 32 || Double.IsNaN(u) || Double.IsNaN(v) || u < 0 || u >= 1 || v < 0 || v >= 1) return HitTarget.None;
      if (roleId != "demo-shapes") return HitTarget.Legacy;
      if (resting) return HitTarget.Resting;
      if (!standing) return HitTarget.Busy;
      if (Ellipse(u, v, .365, .194, .045, .027)) return HitTarget.Mouth;
      if (Ellipse(u, v, .440, .475, .190, .165)) return HitTarget.Belly;
      return HitTarget.Body;
    }
    // Continuous horizontal mapping, shared by rasterization and hit inversion.
    // No band quantization: adjacent rows follow the same smooth belly envelope.
    internal static double ElasticStretch(double normalizedY, int imageHeight, double elastic) {
      if (imageHeight <= 0) throw new ArgumentOutOfRangeException("imageHeight");
      if (Math.Abs(elastic) < .001) return 1;
      return 1 + .06 * elastic * BellyBand(normalizedY);
    }
    internal static double UndoElastic(double u, double v, int imageHeight, double elastic) {
      return .44 + (u - .44) / ElasticStretch(v, imageHeight, elastic);
    }
    // Deform a horizontal band around the calibrated belly, smoothly reaching
    // zero at its edges. This is a procedural effect, never an original action.
    internal static double BellyBand(double normalizedY) {
      double t = (normalizedY - .475) / .165;
      return Math.Abs(t) >= 1 ? 0 : Math.Pow(1 - t * t, 2);
    }
  }
  internal sealed class GestureState {
    private HitTarget pressed, previous;
    private bool doubleClick, active;
    internal void Begin(HitTarget target, bool isDouble) { pressed = target; doubleClick = isDouble; active = target != HitTarget.None; }
    internal PetAction End(HitTarget target, bool moved) {
      if (!active || moved || target != pressed) { Cancel(); return PetAction.None; }
      active = false;
      if (doubleClick) {
        bool split = previous == HitTarget.Body && pressed == HitTarget.Body; previous = HitTarget.None;
        return split ? PetAction.Split : PetAction.None;
      }
      previous = pressed;
      if (pressed == HitTarget.Mouth || pressed == HitTarget.Legacy) return PetAction.Laugh;
      if (pressed == HitTarget.Belly) return PetAction.Bounce;
      if (pressed == HitTarget.Resting) return PetAction.Restore;
      return PetAction.None;
    }
    internal void Cancel() { active = false; pressed = previous = HitTarget.None; }
  }
  // Each explicitly started action gets at most one audio-start opportunity.
  // Suspension/mute revokes it even when the first decoded frame is still pending.
  // SoundPlayer cannot seek; resume must never restart an interrupted clip at zero.
  internal sealed class ActionAudioGate {
    private bool eligible;
    internal void Begin(bool enabled) { eligible = enabled; }
    internal void Suppress() { eligible = false; }
    internal bool TryConsume() { bool result = eligible; eligible = false; return result; }
  }
  internal sealed class VisualEffects {
    private double bounce = 1, age;
    internal int Phase { get; private set; } // 0 none, 1 fade out, 2 await own idle, 3 fade in
    internal bool Recovering { get { return Phase != 0; } }
    internal bool ShowIdle { get { return Phase >= 2; } }
    internal bool Bouncing { get { return bounce < .65; } }
    internal double Elastic { get { return Bouncing ? Math.Exp(-7 * bounce) * Math.Sin(2 * Math.PI * 5 * bounce) : 0; } }
    internal int Stamp { get { return Phase != 0 ? 100 + Phase * 100 + (int)(age * 30) : (Bouncing ? 1 + (int)(bounce * 30) : 0); } }
    private static double Smooth(double t) { t = Math.Max(0, Math.Min(1, t)); return t * t * (3 - 2 * t); }
    internal byte Opacity {
      get { return (byte)Math.Round(255 * (Phase == 1 ? 1 - Smooth(age / .125) : (Phase == 2 ? 0 : (Phase == 3 ? Smooth(age / .125) : 1)))); }
    }
    internal void Bounce() { if (!Recovering) bounce = 0; }
    internal void Recover() { bounce = 1; age = 0; Phase = 1; }
    internal void IdlePresented() { if (Phase == 2) { age = 0; Phase = 3; } }
    internal void Advance(double dt) {
      dt = Math.Max(0, dt); bounce += dt;
      if (Phase == 1 || Phase == 3) {
        age += dt;
        if (age >= .125) { Phase = Phase == 1 ? 2 : 0; age = 0; }
      }
    }
    internal void Cancel() { bounce = 1; age = 0; Phase = 0; }
  }
}
