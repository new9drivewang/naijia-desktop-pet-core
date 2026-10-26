using System;

namespace NaiPet {
  // Operates on the actual already-scaled DIB pixels (BGRA, premultiplied).
  // Whole-image GDI+ scaling is performed once by LayeredSurface. This pass has
  // no DrawImage subrectangles, vertical sampling boundaries, or alpha threshold.
  internal static class ElasticRaster {
    internal static void Warp(byte[] pixels, int width, int height, SpriteLayout layout, double elastic, byte[] row) {
      if (pixels == null || row == null) throw new ArgumentNullException("pixels");
      if (width <= 0 || height <= 0 || width > 2048 || height > 2048 ||
        pixels.Length != checked(width * height * 4) || row.Length < checked(width * 4) ||
        layout.Width <= 0 || layout.Height <= 0 || Double.IsNaN(elastic) || Double.IsInfinity(elastic) || Math.Abs(elastic) > 1)
        throw new ArgumentOutOfRangeException("width", "Invalid elastic raster input");
      if (Math.Abs(elastic) < .001) return;
      int stride = width * 4;
      double anchor = layout.X + .44 * layout.Width;
      for (int y = 0; y < height; y++) {
        double v = (y + .5 - layout.Y) / layout.Height;
        double stretch = BodyZones.ElasticStretch(v, 1, elastic);
        if (stretch == 1) continue; // head, feet and rows outside belly are byte-identical
        int offset = y * stride;
        Buffer.BlockCopy(pixels, offset, row, 0, stride);
        for (int x = 0; x < width; x++) {
          // Pixel-center inverse mapping. Resample the complete row once, with
          // transparent padding beyond the DIB; never sample a previous output.
          double source = anchor + (x + .5 - anchor) / stretch - .5;
          int left = (int)Math.Floor(source), right = left + 1;
          double fraction = source - left;
          int target = offset + x * 4;
          for (int channel = 0; channel < 4; channel++) {
            int a = left >= 0 && left < width ? row[left * 4 + channel] : 0;
            int b = right >= 0 && right < width ? row[right * 4 + channel] : 0;
            pixels[target + channel] = (byte)Math.Floor(a + (b - a) * fraction + .5);
          }
        }
      }
    }
  }
}
