using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NaiPet {
  // UI-thread owned. Allocate once per window size, not once per animation frame.
  // The Bitmap does not own scan0: dispose Graphics/Bitmap before deleting its DIB.
  internal sealed class LayeredSurface : IDisposable {
    private readonly int width, height;
    private IntPtr memoryDC, dib, previousBitmap, scan0;
    private Bitmap bitmap;
    private Graphics graphics;
    private byte[] elasticPixels, elasticRow;
    private bool disposed;
    internal int Width { get { return width; } }
    internal int Height { get { return height; } }

    internal LayeredSurface(int width, int height) {
      if (width <= 0 || height <= 0 || width > 2048 || height > 2048)
        throw new ArgumentOutOfRangeException("width", "绘图缓冲尺寸无效。");
      this.width = width; this.height = height;
      try {
        // NULL creates a memory DC compatible with the current screen; no GetDC lease.
        memoryDC = CreateCompatibleDC(IntPtr.Zero);
        if (memoryDC == IntPtr.Zero) throw NativeFailure("无法创建透明绘图缓冲。");
        BITMAPINFO info = new BITMAPINFO();
        info.header.size = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
        info.header.width = width; info.header.height = -height; // Top-down scanlines.
        info.header.planes = 1; info.header.bitCount = 32; info.header.compression = 0; // BI_RGB
        info.header.sizeImage = checked((uint)(width * height * 4));
        IntPtr bits;
        dib = CreateDIBSection(memoryDC, ref info, 0, out bits, IntPtr.Zero, 0); // DIB_RGB_COLORS
        if (dib == IntPtr.Zero || bits == IntPtr.Zero) throw NativeFailure("无法创建透明位图。");
        scan0 = bits;
        previousBitmap = SelectObject(memoryDC, dib);
        if (previousBitmap == IntPtr.Zero || previousBitmap == new IntPtr(-1)) {
          previousBitmap = IntPtr.Zero;
          throw NativeFailure("无法选择透明绘图位图。");
        }
        // PArgb is essential: UpdateLayeredWindow expects premultiplied alpha.
        bitmap = new Bitmap(width, height, checked(width * 4), PixelFormat.Format32bppPArgb, bits);
        graphics = Graphics.FromImage(bitmap);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.TranslateTransform(width / 2f, height / 2f);
        graphics.Clear(Color.Transparent);
        graphics.Flush(FlushIntention.Sync);
      } catch { Dispose(); throw; }
    }

    internal void Draw(Bitmap sprite, int petSize) { Draw(sprite, petSize, 0); }
    internal void Draw(Bitmap sprite, int petSize, double elastic) {
      ThrowIfDisposed();
      if (sprite == null) throw new ArgumentNullException("sprite");
      if (petSize <= 0) throw new ArgumentOutOfRangeException("petSize");
      // Same aspect ratio, centering and interpolation as v7.2; no source mutation.
      graphics.Clear(Color.Transparent);
      SpriteLayout layout = SpriteLayout.Fit(width, height, petSize, sprite.Width, sprite.Height);
      float w = (float)layout.Width, h = (float)layout.Height;
      graphics.DrawImage(sprite, -w / 2, -h / 2, w, h);
      // Finish GDI+ writes before user32 reads the shared DIB memory.
      graphics.Flush(FlushIntention.Sync);
      if (Math.Abs(elastic) >= .001) {
        // Reuse one DIB-sized buffer and one row; no per-frame allocation/DC/bitmap.
        if (elasticPixels == null) { elasticPixels = new byte[checked(width * height * 4)]; elasticRow = new byte[checked(width * 4)]; }
        Marshal.Copy(scan0, elasticPixels, 0, elasticPixels.Length);
        ElasticRaster.Warp(elasticPixels, width, height, layout, elastic, elasticRow);
        Marshal.Copy(elasticPixels, 0, scan0, elasticPixels.Length);
      }
    }

    internal int AlphaAt(int x, int y) {
      if (disposed || scan0 == IntPtr.Zero || x < 0 || y < 0 || x >= width || y >= height) return 0;
      return Marshal.ReadByte(scan0, (y * width + x) * 4 + 3);
    }
    internal void Present(Form window) { Present(window, 255); }
    internal void Present(Form window, byte opacity) {
      ThrowIfDisposed();
      if (window == null) throw new ArgumentNullException("window");
      if (window.IsDisposed || !window.IsHandleCreated) return;
      POINT destination = new POINT(window.Left, window.Top), source = new POINT(0, 0);
      SIZE size = new SIZE(width, height);
      BLENDFUNCTION blend = new BLENDFUNCTION(); blend.alpha = opacity; blend.format = 1;
      // hdcDst == NULL uses the default palette; no per-frame GetDC/ReleaseDC.
      if (!UpdateLayeredWindow(window.Handle, IntPtr.Zero, ref destination, ref size,
          memoryDC, ref source, 0, ref blend, 2)) // ULW_ALPHA
        throw NativeFailure("无法更新透明桌宠窗口。");
    }

    private void ThrowIfDisposed() {
      if (disposed) throw new ObjectDisposedException("LayeredSurface");
    }
    private static Exception NativeFailure(string message) {
      int error = Marshal.GetLastWin32Error();
      return new Win32Exception(error, message + " (Win32 " + error + ")");
    }
    public void Dispose() {
      if (disposed) return;
      disposed = true;
      // Even a managed cleanup failure must not leak the native drawing objects.
      try {
        try { if (graphics != null) graphics.Dispose(); }
        finally { graphics = null; if (bitmap != null) bitmap.Dispose(); bitmap = null; }
      } finally {
        // Never delete a bitmap while it is selected in a live DC.
        if (previousBitmap != IntPtr.Zero && memoryDC != IntPtr.Zero) {
          SelectObject(memoryDC, previousBitmap); previousBitmap = IntPtr.Zero;
        }
        // Delete the DC first: this also releases selection if restoration failed.
        if (memoryDC != IntPtr.Zero) { DeleteDC(memoryDC); memoryDC = IntPtr.Zero; }
        if (dib != IntPtr.Zero) { DeleteObject(dib); dib = IntPtr.Zero; }
        scan0 = IntPtr.Zero;
        elasticPixels = elasticRow = null;
      }
    }

    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER {
      internal uint size;
      internal int width, height;
      internal ushort planes, bitCount;
      internal uint compression, sizeImage;
      internal int xPelsPerMeter, yPelsPerMeter;
      internal uint colorsUsed, colorsImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFO {
      internal BITMAPINFOHEADER header;
      internal uint unusedColor;
    }
    [StructLayout(LayoutKind.Sequential)] private struct POINT {
      internal int x, y;
      internal POINT(int x, int y) { this.x = x; this.y = y; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE {
      internal int width, height;
      internal SIZE(int width, int height) { this.width = width; this.height = height; }
    }
    [StructLayout(LayoutKind.Sequential, Pack=1)] private struct BLENDFUNCTION {
      internal byte operation, flags, alpha, format;
    }
    [DllImport("gdi32.dll", SetLastError=true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError=true)] private static extern IntPtr CreateDIBSection(
      IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError=true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDC,
      ref POINT destination, ref SIZE size, IntPtr sourceDC, ref POINT source,
      int colorKey, ref BLENDFUNCTION blend, int flags);
  }
}
