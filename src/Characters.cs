using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Xml;
using System.Globalization;

namespace NaiPet {
  internal sealed class RoleFrames {
    internal string[] Paths = new string[0];
    internal double Fps = 30;
  }
  internal sealed class PetRole {
    internal string Id, Name, Directory, Idle, Audio, Source, LicenseNote;
    internal string ActionName = "本角色动作";
    internal int Width, Height, Users;
    internal bool HoldLast = true;
    internal RoleFrames Laugh = new RoleFrames(), Look = new RoleFrames(), BlinkLeft = new RoleFrames(), BlinkRight = new RoleFrames();
    internal double Duration { get { return Laugh.Paths.Length / Laugh.Fps; } }
  }
  internal sealed partial class RoleCatalog : IDisposable {
    internal readonly List<PetRole> Roles = new List<PetRole>();
    internal readonly List<string> Warnings = new List<string>();
    internal PetRole Default { get { return selectedDefault ?? Roles[0]; } }
    internal RoleCatalog(string root) {
      if (String.IsNullOrWhiteSpace(root)) throw new InvalidDataException("Missing demo directory; use Launch.cmd.");
      root = Path.GetFullPath(root); // All runtime asset paths are absolute; never depend on the current working directory.
      // Only the original code-generated geometric demo is built in.
      string demo = SafePath(root, "assets/demo/character.xml", ".xml");
      Roles.Add(Parse(Path.Combine(root, "assets", "demo"), demo));
      string packs = Path.Combine(root, "characters");
      if (!System.IO.Directory.Exists(packs)) return;
      if ((File.GetAttributes(packs) & FileAttributes.ReparsePoint) != 0) { Warnings.Add("characters目录是链接，未加载。"); return; }
      int inspected = 0;
      foreach (string folder in System.IO.Directory.GetDirectories(packs)) {
        if (++inspected > 64) { Warnings.Add("仅检查前64个角色目录，请移除无效包后重启。"); break; }
        if (Roles.Count >= 24) { Warnings.Add("最多加载24种角色。"); break; }
        try {
          string manifest = SafePath(folder, "character.xml", ".xml");
          if (!File.Exists(manifest)) continue;
          PetRole role = Parse(folder, manifest);
          bool duplicate = false; foreach (PetRole existing in Roles) if (existing.Id == role.Id) duplicate = true;
          if (duplicate) throw new InvalidDataException("角色ID重复");
          Roles.Add(role);
        } catch (Exception ex) { Warnings.Add(Path.GetFileName(folder) + "：" + ex.Message); }
      }
    }
    internal static string SafePath(string folder, string relative, string extension) {
      if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0) throw new InvalidDataException("素材路径必须是包内相对路径");
      foreach (string part in relative.Replace('\\', '/').Split('/')) if (part == ".." || part == "." || part.Length == 0) throw new InvalidDataException("不允许路径跳转");
      folder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
      string path = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));
      if (!path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("素材越出角色目录");
      if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("素材类型不允许");
      string check = path;
      while (check != null && check.Length >= folder.Length) {
        if ((File.Exists(check) || System.IO.Directory.Exists(check)) && (File.GetAttributes(check) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("不允许链接素材");
        if (String.Equals(check, folder, StringComparison.OrdinalIgnoreCase)) break;
        check = Path.GetDirectoryName(check);
      }
      return path;
    }
    private static int Number(XmlElement element, string name, int min, int max) {
      int value; if (!Int32.TryParse(element.GetAttribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < min || value > max) throw new InvalidDataException(name + "超出范围"); return value;
    }
    private static PetRole Parse(string folder, string manifest) {
      XmlReaderSettings settings = new XmlReaderSettings(); settings.DtdProcessing = DtdProcessing.Prohibit; settings.XmlResolver = null; settings.MaxCharactersInDocument = 100000;
      XmlDocument doc = new XmlDocument(); doc.XmlResolver = null;
      using (XmlReader reader = XmlReader.Create(manifest, settings)) doc.Load(reader);
      XmlElement root = doc.DocumentElement; if (root == null || root.Name != "character") throw new InvalidDataException("缺少character根节点");
      PetRole role = new PetRole(); role.Directory = Path.GetFullPath(folder); role.Id = root.GetAttribute("id"); role.Name = root.GetAttribute("name");
      if (role.Id.Length < 1 || role.Id.Length > 50 || role.Name.Length < 1 || role.Name.Length > 80) throw new InvalidDataException("角色名称或ID无效");
      role.Width = Number(root, "width", 16, 1024); role.Height = Number(root, "height", 16, 1024);
      role.Source = root.GetAttribute("source"); role.LicenseNote = root.GetAttribute("license");
      if (role.Source.Length == 0 || role.LicenseNote.Length == 0) throw new InvalidDataException("需标来源及许可说明，填写不等于已获授权");
      role.HoldLast = root.GetAttribute("end") != "idle";
      string actionName = root.GetAttribute("action"); if (actionName.Length > 0 && actionName.Length <= 80) role.ActionName = actionName;
      XmlElement idle = root["idle"]; if (idle == null) throw new InvalidDataException("必须提供该角色自身idle PNG");
      role.Idle = SafePath(folder, idle.GetAttribute("path"), ".png"); CheckHeader(role.Idle, role);
      role.Laugh = Sequence(root["laugh"], role); role.Look = Sequence(root["look"], role);
      role.BlinkLeft = Sequence(root["blinkLeft"], role); role.BlinkRight = Sequence(root["blinkRight"], role);
      XmlElement audio = root["audio"];
      if (audio != null) { string path = SafePath(folder, audio.GetAttribute("path"), ".wav"); if (File.Exists(path) && new FileInfo(path).Length <= 20 * 1024 * 1024) role.Audio = path; }
      return role;
    }
    private static RoleFrames Sequence(XmlElement element, PetRole role) {
      RoleFrames result = new RoleFrames(); if (element == null) return result;
      int maxCount = element.Name == "laugh" ? 1200 : (element.Name == "look" ? 64 : 120);
      int count = Number(element, "count", 1, maxCount), digits = Number(element, "digits", 1, 6);
      double fps; if (!Double.TryParse(element.GetAttribute("fps"), NumberStyles.Float, CultureInfo.InvariantCulture, out fps) || Double.IsNaN(fps) || Double.IsInfinity(fps) || fps < 1 || fps > 60) throw new InvalidDataException("帧率必须为1–60");
      int start = element.HasAttribute("start") ? Number(element, "start", 0, 999999) : 0;
      result.Fps = fps; result.Paths = new string[count];
      for (int i = 0; i < count; i++) { result.Paths[i] = SafePath(role.Directory, element.GetAttribute("directory") + "/" + (i + start).ToString(new string('0', digits)) + ".png", ".png"); CheckHeader(result.Paths[i], role); }
      return result;
    }
    private static void CheckHeader(string path, PetRole role) {
      if (!File.Exists(path)) throw new FileNotFoundException("缺少素材文件：\n" + path + "\n\nExtract the complete demo ZIP before starting. Run Check.cmd to verify files.", path);
      long length = new FileInfo(path).Length;
      if (length > 8 * 1024 * 1024) throw new InvalidDataException("单张PNG超过8MiB（8388608字节）：\n" + path + "\n实际大小：" + length + "字节。Use a valid PNG within the size limit.");
      byte[] header = new byte[24]; using (FileStream stream = File.OpenRead(path)) if (stream.Read(header, 0, header.Length) != header.Length) throw new InvalidDataException("PNG头不完整：" + path);
      byte[] signature = { 137,80,78,71,13,10,26,10 }; for (int i = 0; i < 8; i++) if (header[i] != signature[i]) throw new InvalidDataException("不是PNG：" + path);
      if (header[8] != 0 || header[9] != 0 || header[10] != 0 || header[11] != 13 || header[12] != 73 || header[13] != 72 || header[14] != 68 || header[15] != 82) throw new InvalidDataException("首PNG块必须为13字节IHDR：" + path);
      int width = (header[16]<<24) | (header[17]<<16) | (header[18]<<8) | header[19];
      int height = (header[20]<<24) | (header[21]<<16) | (header[22]<<8) | header[23];
      if (width != role.Width || height != role.Height) throw new InvalidDataException("PNG尺寸与角色声明不一致：" + path);
    }
    internal static string AssetPath(PetRole role, string path, string extension) {
      string full = Path.GetFullPath(path), prefix = role.Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
      if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("素材越出角色目录");
      return SafePath(role.Directory, full.Substring(prefix.Length), extension);
    }

  }
}

namespace NaiPet {
  internal static class PackageCheck {
    internal static string Verify(string root) {
      if (String.IsNullOrWhiteSpace(root)) return "自检失败：未提供桌宠目录。";
      root = Path.GetFullPath(root);
      string manifest = RoleCatalog.SafePath(root, "runtime-files.tsv", ".tsv");
      if (!File.Exists(manifest)) return "自检失败：缺少运行文件清单：\n" + manifest + "\n请完整解压运行包。";
      if (new FileInfo(manifest).Length > 1024 * 1024) return "自检失败：运行文件清单超过1MiB限制。";
      List<string> errors = new List<string>();
      int checkedFiles = 0, decodedPng = 0;
      string[] lines = File.ReadAllLines(manifest, System.Text.Encoding.UTF8);
      if (lines.Length > 3000) return "自检失败：运行文件清单异常。";
      using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create()) {
        foreach (string line in lines) {
          if (String.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
          string label = "清单项目";
          try {
            string[] fields = line.Split('\t'); long expectedSize;
            if (fields.Length != 3 || fields[0].Length != 64 || !Int64.TryParse(fields[1], out expectedSize) || expectedSize < 0)
              throw new InvalidDataException("运行文件清单格式错误");
            label = fields[2];
            string path = RoleCatalog.SafePath(root, label, Path.GetExtension(label));
            if (!File.Exists(path)) { errors.Add("缺少：" + label); continue; }
            if (new FileInfo(path).Length != expectedSize) { errors.Add("大小不匹配：" + label); continue; }
            string actual;
            using (FileStream stream = File.OpenRead(path)) actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if (!String.Equals(actual, fields[0], StringComparison.OrdinalIgnoreCase)) { errors.Add("内容校验不匹配：" + label); continue; }
            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) {
              using (Image decoded = Image.FromFile(path)) {
                using (Bitmap bitmap = new Bitmap(decoded)) { if (bitmap.Width < 1 || bitmap.Height < 1) throw new InvalidDataException("图片解码失败"); }
              }
              decodedPng++;
            }
            checkedFiles++;
          } catch (Exception ex) { errors.Add(label + "：" + ex.Message); }
        }
      }
      if (checkedFiles == 0 && errors.Count == 0) errors.Add("运行文件清单为空");
      if (errors.Count > 0) {
        int shown = Math.Min(errors.Count, 12);
        string report = "自检未通过：共" + errors.Count + "项缺失、损坏或不可读取。\n查找目录：" + root + "\n\n";
        for (int i = 0; i < shown; i++) report += errors[i] + "\n";
        if (errors.Count > shown) report += "另有" + (errors.Count - shown) + "项。\n";
        return report + "\nExtract the complete demo ZIP; do not launch from inside the archive.";
      }
      try {
        using (RoleCatalog catalog = new RoleCatalog(root)) {
          return "PASS: " + checkedFiles + "个文件SHA256匹配，" + decodedPng + "张PNG解码成功，已加载" + catalog.Roles.Count + "个角色。\n目录：" + root
            + (catalog.Warnings.Count == 0 ? "" : "\n额外角色提示：\n" + String.Join("\n", catalog.Warnings.ToArray()))
            + "\n这只验证本机文件读取/解码与角色清单；透明窗口、音频和交互请通过Launch.cmd体验。";
        }
      } catch (Exception ex) { return "文件校验通过，但角色加载失败：\n" + ex.Message; }
    }
  }
}
