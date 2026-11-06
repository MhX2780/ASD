using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WGI = Windows.Graphics.Imaging;

namespace ASD;

/// <summary>Shared image code: loading (raster + SVG), resizing, and encoding to PNG/JPG/BMP/GIF/TIFF/ICO/SVG.
/// All pixel buffers are premultiplied BGRA unless a method says "straight".</summary>
internal static class ImageTools
{
    public static async Task<(byte[] px, int w, int h)> LoadRasterAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await WGI.BitmapDecoder.CreateAsync(stream);
        WGI.BitmapFrame frame = await decoder.GetFrameAsync(0);
        if (decoder.FrameCount > 1 && Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            // an .ico holds several sizes: take the largest
            for (uint i = 1; i < decoder.FrameCount; i++)
            {
                var f = await decoder.GetFrameAsync(i);
                if ((long)f.PixelWidth * f.PixelHeight > (long)frame.PixelWidth * frame.PixelHeight) frame = f;
            }
        }
        var data = await frame.GetPixelDataAsync(
            WGI.BitmapPixelFormat.Bgra8, WGI.BitmapAlphaMode.Premultiplied, new WGI.BitmapTransform(),
            WGI.ExifOrientationMode.RespectExifOrientation, WGI.ColorManagementMode.DoNotColorManage);
        return (data.DetachPixelData(), (int)frame.OrientedPixelWidth, (int)frame.OrientedPixelHeight);
    }

    /// <summary>Renders an SVG through the visual tree. <paramref name="host"/> must be a visible square Grid that contains <paramref name="image"/>.</summary>
    public static async Task<(byte[] px, int w, int h)> RenderSvgAsync(string path, Grid host, Image image)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var svg = new SvgImageSource { RasterizePixelWidth = 1024, RasterizePixelHeight = 1024 };
        var status = await svg.SetSourceAsync(stream);
        if (status != SvgImageSourceLoadStatus.Success)
            throw new InvalidOperationException($"Could not read this SVG ({status}).");

        image.Source = svg;
        await Task.Delay(250);
        host.UpdateLayout();
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(host, 1024, 1024);
        var buf = await rtb.GetPixelsAsync();
        return (buf.ToArray(), rtb.PixelWidth, rtb.PixelHeight);
    }

    // ───────── encoding to any target format ─────────
    public static async Task<byte[]> EncodeAsync(string format, byte[] px, int w, int h,
        int jpegQuality = 90, bool whiteBackground = true, List<int>? icoSizes = null)
    {
        switch (format)
        {
            case "png":
                return await EncodePng(ToStraight(px), w, h);
            case "jpg":
                return await EncodeWith(WGI.BitmapEncoder.JpegEncoderId, Flatten(px, whiteBackground ? (byte)255 : (byte)0),
                    w, h, WGI.BitmapAlphaMode.Ignore, Math.Clamp(jpegQuality, 1, 100) / 100f);
            case "bmp":
                return await EncodeWith(WGI.BitmapEncoder.BmpEncoderId, ToStraight(px), w, h, WGI.BitmapAlphaMode.Straight, null);
            case "gif":
                return await EncodeWith(WGI.BitmapEncoder.GifEncoderId, ToStraight(px), w, h, WGI.BitmapAlphaMode.Straight, null);
            case "tif":
                return await EncodeWith(WGI.BitmapEncoder.TiffEncoderId, ToStraight(px), w, h, WGI.BitmapAlphaMode.Straight, null);
            case "ico":
            {
                var sizes = icoSizes is { Count: > 0 } ? icoSizes : new List<int> { 16, 32, 48, 64, 128, 256 };
                var m = await Task.Run(() => BuildMaster(px, w, h, false));
                return await BuildIco(m.px, m.side, sizes);
            }
            case "svg":
            {
                var png = await EncodePng(ToStraight(px), w, h);
                var svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"{w}\" height=\"{h}\" viewBox=\"0 0 {w} {h}\">\n" +
                          $"  <image width=\"{w}\" height=\"{h}\" xlink:href=\"data:image/png;base64,{Convert.ToBase64String(png)}\"/>\n</svg>\n";
                return Encoding.UTF8.GetBytes(svg);
            }
            default:
                throw new NotSupportedException("Unknown target format: " + format);
        }
    }

    private static byte[] Flatten(byte[] px, byte bg)
    {
        var o = new byte[px.Length];
        for (int i = 0; i < px.Length; i += 4)
        {
            int inv = 255 - px[i + 3];
            o[i] = (byte)Math.Min(255, px[i] + bg * inv / 255);
            o[i + 1] = (byte)Math.Min(255, px[i + 1] + bg * inv / 255);
            o[i + 2] = (byte)Math.Min(255, px[i + 2] + bg * inv / 255);
            o[i + 3] = 255;
        }
        return o;
    }

    private static async Task<byte[]> EncodeWith(Guid encoderId, byte[] px, int w, int h, WGI.BitmapAlphaMode alpha, float? quality)
    {
        using var ms = new InMemoryRandomAccessStream();
        WGI.BitmapEncoder enc;
        if (quality != null)
        {
            var props = new WGI.BitmapPropertySet
            {
                { "ImageQuality", new WGI.BitmapTypedValue(quality.Value, Windows.Foundation.PropertyType.Single) },
            };
            enc = await WGI.BitmapEncoder.CreateAsync(encoderId, ms, props);
        }
        else enc = await WGI.BitmapEncoder.CreateAsync(encoderId, ms);

        enc.SetPixelData(WGI.BitmapPixelFormat.Bgra8, alpha, (uint)w, (uint)h, 96, 96, px);
        await enc.FlushAsync();
        return await ReadAll(ms);
    }

    private static async Task<byte[]> ReadAll(InMemoryRandomAccessStream ms)
    {
        var bytes = new byte[ms.Size];
        using var reader = new DataReader(ms.GetInputStreamAt(0));
        await reader.LoadAsync((uint)ms.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    public static WriteableBitmap MakeBitmap(byte[] px, int w, int h)
    {
        var wb = new WriteableBitmap(w, h);
        px.CopyTo(wb.PixelBuffer);
        wb.Invalidate();
        return wb;
    }

    public static async Task<byte[]> BuildIco(byte[] master, int side, List<int> sizes)
    {
        var images = new List<(int Size, byte[] Data)>();
        foreach (var s in sizes)
        {
            var px = await Task.Run(() => ToStraight(Resize(master, side, side, s, s)));
            byte[] data = s >= 256 ? await EncodePng(px, s, s) : BuildDib(px, s);
            images.Add((s, data));
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((short)0);                  // reserved
        bw.Write((short)1);                  // type: icon
        bw.Write((short)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (s, d) in images)
        {
            byte dim = (byte)(s >= 256 ? 0 : s);
            bw.Write(dim); bw.Write(dim);
            bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((short)1);              // planes
            bw.Write((short)32);             // bits per pixel
            bw.Write(d.Length);
            bw.Write(offset);
            offset += d.Length;
        }
        foreach (var (_, d) in images) bw.Write(d);
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>Classic 32-bit DIB icon image (BITMAPINFOHEADER + BGRA rows bottom-up + AND mask).</summary>
    public static byte[] BuildDib(byte[] straightBgra, int size)
    {
        int maskRow = (size + 31) / 32 * 4;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(40);                         // header size
        bw.Write(size);                       // width
        bw.Write(size * 2);                   // height (XOR + AND mask)
        bw.Write((short)1);                   // planes
        bw.Write((short)32);                  // bpp
        bw.Write(0);                          // compression
        bw.Write(size * size * 4 + maskRow * size);
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        for (int y = size - 1; y >= 0; y--) bw.Write(straightBgra, y * size * 4, size * 4);
        bw.Write(new byte[maskRow * size]);   // AND mask: alpha channel does the work
        bw.Flush();
        return ms.ToArray();
    }

    public static async Task<byte[]> EncodePng(byte[] straightBgra, int w, int h)
    {
        using var ms = new InMemoryRandomAccessStream();
        var enc = await WGI.BitmapEncoder.CreateAsync(WGI.BitmapEncoder.PngEncoderId, ms);
        enc.SetPixelData(WGI.BitmapPixelFormat.Bgra8, WGI.BitmapAlphaMode.Straight, (uint)w, (uint)h, 96, 96, straightBgra);
        await enc.FlushAsync();
        var bytes = new byte[ms.Size];
        using var reader = new DataReader(ms.GetInputStreamAt(0));
        await reader.LoadAsync((uint)ms.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    // ───────── pixel helpers (premultiplied BGRA) ─────────
    public static (byte[] px, int side) BuildMaster(byte[] src, int w, int h, bool fill)
    {
        int side;
        byte[] sq;
        if (fill)
        {
            side = Math.Min(w, h);
            sq = new byte[side * side * 4];
            int ox = (w - side) / 2, oy = (h - side) / 2;
            for (int y = 0; y < side; y++)
                System.Buffer.BlockCopy(src, ((y + oy) * w + ox) * 4, sq, y * side * 4, side * 4);
        }
        else
        {
            side = Math.Max(w, h);
            sq = new byte[side * side * 4];
            int ox = (side - w) / 2, oy = (side - h) / 2;
            for (int y = 0; y < h; y++)
                System.Buffer.BlockCopy(src, y * w * 4, sq, ((y + oy) * side + ox) * 4, w * 4);
        }
        if (side > 1024) { sq = Resize(sq, side, side, 1024, 1024); side = 1024; }
        return (sq, side);
    }

    public static (int[][] Idx, float[][] Wt) Weights(int srcLen, int dstLen)
    {
        var idx = new int[dstLen][];
        var wt = new float[dstLen][];
        double scale = (double)srcLen / dstLen;
        for (int i = 0; i < dstLen; i++)
        {
            if (scale > 1)   // shrinking: average every source pixel that falls into this one
            {
                double start = i * scale, end = (i + 1) * scale;
                int k0 = (int)Math.Floor(start), k1 = Math.Min(srcLen - 1, (int)Math.Ceiling(end) - 1);
                var ii = new List<int>();
                var ww = new List<float>();
                for (int k = k0; k <= k1; k++)
                {
                    double w = Math.Min(end, k + 1) - Math.Max(start, k);
                    if (w > 0) { ii.Add(k); ww.Add((float)(w / scale)); }
                }
                idx[i] = ii.ToArray();
                wt[i] = ww.ToArray();
            }
            else             // enlarging: bilinear
            {
                double pos = (i + 0.5) * scale - 0.5;
                int k0 = (int)Math.Floor(pos);
                float f = (float)(pos - k0);
                int a = Math.Clamp(k0, 0, srcLen - 1), b = Math.Clamp(k0 + 1, 0, srcLen - 1);
                if (a == b) { idx[i] = new[] { a }; wt[i] = new[] { 1f }; }
                else { idx[i] = new[] { a, b }; wt[i] = new[] { 1 - f, f }; }
            }
        }
        return (idx, wt);
    }

    public static byte[] Resize(byte[] src, int sw, int sh, int dw, int dh)
    {
        if (sw == dw && sh == dh) return (byte[])src.Clone();
        var (xi, xw) = Weights(sw, dw);
        var (yi, yw) = Weights(sh, dh);

        var tmp = new float[dw * sh * 4];
        for (int y = 0; y < sh; y++)
        {
            int rowSrc = y * sw * 4, rowTmp = y * dw * 4;
            for (int x = 0; x < dw; x++)
            {
                float b = 0, g = 0, r = 0, a = 0;
                var ids = xi[x];
                var ws = xw[x];
                for (int k = 0; k < ids.Length; k++)
                {
                    int p = rowSrc + ids[k] * 4;
                    float wg = ws[k];
                    b += src[p] * wg; g += src[p + 1] * wg; r += src[p + 2] * wg; a += src[p + 3] * wg;
                }
                int o = rowTmp + x * 4;
                tmp[o] = b; tmp[o + 1] = g; tmp[o + 2] = r; tmp[o + 3] = a;
            }
        }

        var dst = new byte[dw * dh * 4];
        for (int y = 0; y < dh; y++)
        {
            var ids = yi[y];
            var ws = yw[y];
            for (int x = 0; x < dw; x++)
            {
                float b = 0, g = 0, r = 0, a = 0;
                for (int k = 0; k < ids.Length; k++)
                {
                    int p = (ids[k] * dw + x) * 4;
                    float wg = ws[k];
                    b += tmp[p] * wg; g += tmp[p + 1] * wg; r += tmp[p + 2] * wg; a += tmp[p + 3] * wg;
                }
                int o = (y * dw + x) * 4;
                dst[o] = (byte)Math.Clamp((int)(b + 0.5f), 0, 255);
                dst[o + 1] = (byte)Math.Clamp((int)(g + 0.5f), 0, 255);
                dst[o + 2] = (byte)Math.Clamp((int)(r + 0.5f), 0, 255);
                dst[o + 3] = (byte)Math.Clamp((int)(a + 0.5f), 0, 255);
            }
        }
        return dst;
    }

    /// <summary>
    /// Makes the background transparent. Input and output are premultiplied BGRA.
    /// The background colour is detected from the four corners.
    /// <paramref name="tolerancePercent"/> = how far a colour may differ from the background (0-100).
    /// <paramref name="edgesOnly"/> = only remove background that touches the image border,
    /// so same-coloured areas inside the subject (e.g. a white eye) are kept.
    /// </summary>
    public static byte[] RemoveBackground(byte[] px, int w, int h, int tolerancePercent, bool edgesOnly)
    {
        var s = ToStraight(px);
        int n = w * h;

        // 1) background colour = the corner colour that agrees most with the other corners
        int[] cornerIdx = { 0, w - 1, (h - 1) * w, n - 1 };
        int best = 0, bestScore = int.MaxValue;
        for (int a = 0; a < 4; a++)
        {
            int score = 0;
            for (int b = 0; b < 4; b++) score += ColorDist(s, cornerIdx[a] * 4, s, cornerIdx[b] * 4);
            if (score < bestScore) { bestScore = score; best = a; }
        }
        int bo = cornerIdx[best] * 4;
        int bb = s[bo], bg = s[bo + 1], br = s[bo + 2];

        // 2) which pixels look like background
        int t = Math.Clamp(tolerancePercent, 0, 100) * 255 / 100;
        var dist = new int[n];
        var cand = new bool[n];
        for (int p = 0; p < n; p++)
        {
            int i = p * 4;
            if (s[i + 3] == 0) { dist[p] = 0; cand[p] = true; continue; }
            int d = Math.Max(Math.Abs(s[i] - bb), Math.Max(Math.Abs(s[i + 1] - bg), Math.Abs(s[i + 2] - br)));
            dist[p] = d;
            cand[p] = d <= t;
        }

        // 3) decide what is removed
        var removed = new bool[n];
        if (!edgesOnly)
        {
            Array.Copy(cand, removed, n);
        }
        else
        {
            var queue = new int[n];
            int head = 0, tail = 0;
            void Push(int p) { if (cand[p] && !removed[p]) { removed[p] = true; queue[tail++] = p; } }
            for (int x = 0; x < w; x++) { Push(x); Push((h - 1) * w + x); }
            for (int y = 0; y < h; y++) { Push(y * w); Push(y * w + w - 1); }
            while (head < tail)
            {
                int p = queue[head++];
                int x = p % w, y = p / w;
                if (x > 0) Push(p - 1);
                if (x < w - 1) Push(p + 1);
                if (y > 0) Push(p - w);
                if (y < h - 1) Push(p + w);
            }
        }

        // 4) write result: removed pixels -> transparent; pixels right next to them get a soft edge
        //    and their background tint is taken out so no white halo remains
        var o = new byte[px.Length];
        int soft = Math.Max(1, t * 8 / 10);
        for (int p = 0; p < n; p++)
        {
            int i = p * 4;
            if (removed[p]) continue;                       // stays 0,0,0,0

            int alpha = s[i + 3];
            byte cb = s[i], cg = s[i + 1], cr = s[i + 2];

            if (t > 0 && dist[p] < t + soft && NextToRemoved(removed, p, w, h))
            {
                int a = Math.Clamp((dist[p] - t) * 255 / soft, 0, 255);
                if (a < 255 && a > 0)
                {
                    cb = Unmix(cb, bb, a); cg = Unmix(cg, bg, a); cr = Unmix(cr, br, a);
                    alpha = alpha * a / 255;
                }
                else if (a == 0) continue;
            }

            o[i] = (byte)(cb * alpha / 255);
            o[i + 1] = (byte)(cg * alpha / 255);
            o[i + 2] = (byte)(cr * alpha / 255);
            o[i + 3] = (byte)alpha;
        }
        return o;
    }

    private static int ColorDist(byte[] a, int ia, byte[] b, int ib)
        => Math.Max(Math.Abs(a[ia] - b[ib]), Math.Max(Math.Abs(a[ia + 1] - b[ib + 1]), Math.Abs(a[ia + 2] - b[ib + 2])));

    private static byte Unmix(byte c, int bgc, int a)
        => (byte)Math.Clamp((c * 255 - bgc * (255 - a)) / Math.Max(a, 1), 0, 255);

    private static bool NextToRemoved(bool[] removed, int p, int w, int h)
    {
        int x = p % w, y = p / w;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                if (removed[ny * w + nx]) return true;
            }
        return false;
    }

    public static byte[] ToStraight(byte[] px)
    {
        var o = new byte[px.Length];
        for (int i = 0; i < px.Length; i += 4)
        {
            int a = px[i + 3];
            if (a == 0) continue;
            if (a == 255) { o[i] = px[i]; o[i + 1] = px[i + 1]; o[i + 2] = px[i + 2]; o[i + 3] = 255; continue; }
            o[i] = (byte)Math.Min(255, (px[i] * 255 + a / 2) / a);
            o[i + 1] = (byte)Math.Min(255, (px[i + 1] * 255 + a / 2) / a);
            o[i + 2] = (byte)Math.Min(255, (px[i + 2] * 255 + a / 2) / a);
            o[i + 3] = (byte)a;
        }
        return o;
    }
}
