using System;
using System.Collections.Generic;

namespace NaiPet {
  internal sealed partial class RoleCatalog {
    private PetRole selectedDefault;
    private readonly HashSet<PetRole> unavailable = new HashSet<PetRole>();
    internal void ChooseStartup(string preferredId) {
      PetRole preferred = Roles.Find(delegate(PetRole r) { return r.Id == preferredId; });
      if (preferred != null) {
        try { PrepareIdle(preferred); selectedDefault = preferred; return; }
        catch (Exception ex) { unavailable.Add(preferred); Warnings.Add(preferred.Name + "待机图无法解码，已回退：" + ex.Message); }
      } else Warnings.Add("未找到首选角色 " + preferredId + "，已回退到可用角色。");
      selectedDefault = FindFallback(null);
      if (selectedDefault == null) throw new InvalidOperationException("没有可解码的角色待机图，请重新解压完整包。");
    }
    internal PetRole FindFallback(PetRole broken) {
      if (broken != null) unavailable.Add(broken);
      foreach (PetRole role in Roles) {
        if (unavailable.Contains(role)) continue;
        try { PrepareIdle(role); if (selectedDefault == broken) selectedDefault = role; return role; }
        catch (Exception ex) { unavailable.Add(role); Warnings.Add(role.Name + "：" + ex.Message); }
      }
      return null;
    }
  }
}
