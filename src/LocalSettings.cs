using System;
namespace NaiPet {
  internal sealed class PetSettings {
    internal bool Sound = false, AutoStand = true, RecoveryTransition = true;
    internal string PreferredRole = "demo-shapes";
  }
  // Offline public demo: preferences are session-only. Never touch user config.
  internal static class LocalSettings {
    internal static string DirectoryPath { get { return ""; } }
    internal static PetSettings Load(string directory, out string warning) {
      warning = null; return new PetSettings();
    }
    internal static void Save(string directory, PetSettings settings) { }
  }
}
