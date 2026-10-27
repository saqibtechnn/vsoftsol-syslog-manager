using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace VSoftSol.Syslog.BrandingGen;

/// <summary>Outcome of a generation run.</summary>
public sealed record BrandingResult(IReadOnlyList<string> Warnings, string GeneratedInfoPath);

/// <summary>
/// Reads <c>branding/logo.png</c> and <c>branding/brand.json</c> and derives every
/// product asset the operator did not supply, per BRANDING.md. A missing
/// <c>logo.png</c> produces a warning and a placeholder fallback, never a failure.
/// Every write is content-compared first, so two runs produce byte-identical output.
/// </summary>
[SupportedOSPlatform("windows")]
public static class BrandingGenerator
{
    private static readonly Brand Defaults = new(
        ProductName: "VSoftSol Syslog Manager",
        VendorName: "Vision Software Solutions",
        VendorUrl: "https://vsoftsol.com",
        SupportEmail: "support@vsoftsol.com",
        PrimaryColor: "#0F4C81",
        AccentColor: "#2E9E6B",
        Copyright: "© 2026 Vision Software Solutions");

    public static BrandingResult Generate(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        var warnings = new List<string>();

        string brandingDir = Path.Combine(repoRoot, "branding");
        string placeholderDir = Path.Combine(brandingDir, "placeholder");
        string coreGenDir = Path.Combine(repoRoot, "src", "VSoftSol.Syslog.Core", "Generated");
        string webDir = Path.Combine(repoRoot, "src", "VSoftSol.Syslog.Web", "wwwroot", "branding");
        string installerDir = Path.Combine(repoRoot, "installer", "assets");
        string installerRoot = Path.Combine(repoRoot, "installer");

        Directory.CreateDirectory(coreGenDir);
        Directory.CreateDirectory(webDir);
        Directory.CreateDirectory(installerDir);

        Brand brand = LoadBrand(Path.Combine(brandingDir, "brand.json"));

        string infoPath = Path.Combine(coreGenDir, "BrandingInfo.g.cs");
        WriteTextIfDifferent(infoPath, RenderBrandingInfo(brand), warnings);
        WriteTextIfDifferent(Path.Combine(webDir, "brand.css"), RenderBrandCss(brand), warnings);

        // PHASE_12: the installer's product name / manufacturer / vendor URL / support
        // email must be brand data too (BRANDING.md, CLAUDE.md Constraint 11) — the
        // white-label acceptance test rebuilds the MSI after only editing branding/, with
        // no source change to installer/*.wxs.
        WriteTextIfDifferent(Path.Combine(installerRoot, "Strings.en-us.wxl"), RenderWxl(brand), warnings);

        string operatorLogo = Path.Combine(brandingDir, "logo.png");
        string placeholderLogo = Path.Combine(placeholderDir, "logo.png");
        string sourceLogo;
        if (File.Exists(operatorLogo))
        {
            sourceLogo = operatorLogo;
        }
        else if (File.Exists(placeholderLogo))
        {
            warnings.Add($"branding/logo.png not found; using branding/placeholder/logo.png. " +
                         $"Drop a real logo.png into branding/ and rebuild to rebrand.");
            sourceLogo = placeholderLogo;
        }
        else
        {
            warnings.Add("Neither branding/logo.png nor branding/placeholder/logo.png exists; " +
                         "deriving assets from a solid-colour placeholder.");
            sourceLogo = string.Empty;
        }

        using Bitmap master = sourceLogo.Length > 0
            ? LoadBitmap(sourceLogo)
            : SolidPlaceholder(brand.PrimaryColor);

        EmitPng(Path.Combine(webDir, "logo.png"), Square(master, 512), warnings);
        EmitOverridablePng(brandingDir, Path.Combine(webDir, "logo-wide.png"), "logo-wide.png",
            () => Letterbox(master, 800, 200), warnings);
        EmitOverridablePng(brandingDir, Path.Combine(webDir, "logo-mono.png"), "logo-mono.png",
            () => Monochrome(master, brand.PrimaryColor), warnings);

        byte[] favicon = OverrideOrIco(brandingDir, "favicon.ico", master, [16, 32, 48]);
        WriteBytesIfDifferent(Path.Combine(webDir, "favicon.ico"), favicon, warnings);

        byte[] appIcon = OverrideOrIco(brandingDir, "app-icon.ico", master, [16, 32, 48, 256]);
        WriteBytesIfDifferent(Path.Combine(webDir, "app-icon.ico"), appIcon, warnings);
        WriteBytesIfDifferent(Path.Combine(installerDir, "app-icon.ico"), appIcon, warnings);

        byte[] banner = OverrideOrBmp(brandingDir, "installer-banner.bmp", master, 493, 58, brand.PrimaryColor);
        WriteBytesIfDifferent(Path.Combine(installerDir, "installer-banner.bmp"), banner, warnings);

        byte[] dialog = OverrideOrBmp(brandingDir, "installer-dialog.bmp", master, 493, 312, brand.PrimaryColor);
        WriteBytesIfDifferent(Path.Combine(installerDir, "installer-dialog.bmp"), dialog, warnings);

        return new BrandingResult(warnings, infoPath);
    }

    private static Brand LoadBrand(string path)
    {
        if (!File.Exists(path))
        {
            return Defaults;
        }

        string json = File.ReadAllText(path);
        string Get(string key, string fallback)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : fallback;
        }

        return new Brand(
            Get("productName", Defaults.ProductName),
            Get("vendorName", Defaults.VendorName),
            Get("vendorUrl", Defaults.VendorUrl),
            Get("supportEmail", Defaults.SupportEmail),
            NormalizeHex(Get("primaryColor", Defaults.PrimaryColor), Defaults.PrimaryColor),
            NormalizeHex(Get("accentColor", Defaults.AccentColor), Defaults.AccentColor),
            Get("copyright", Defaults.Copyright));
    }

    private static string NormalizeHex(string value, string fallback) =>
        Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$") ? value.ToUpperInvariant() : fallback;

    private static string RenderBrandingInfo(Brand b)
    {
        var sb = new StringBuilder();
        sb.Append("// <auto-generated>\n");
        sb.Append("//     Generated by VSoftSol.Syslog.BrandingGen from branding/brand.json.\n");
        sb.Append("//     Do not edit. Do not commit. See BRANDING.md.\n");
        sb.Append("// </auto-generated>\n");
        sb.Append("#nullable enable\n\n");
        sb.Append("namespace VSoftSol.Syslog.Core;\n\n");
        sb.Append("/// <summary>Compile-time product identity. The only place brand strings are literals.</summary>\n");
        sb.Append("public static class BrandingInfo\n");
        sb.Append("{\n");
        sb.Append($"    public const string ProductName = {Literal(b.ProductName)};\n");
        sb.Append($"    public const string VendorName = {Literal(b.VendorName)};\n");
        sb.Append($"    public const string VendorUrl = {Literal(b.VendorUrl)};\n");
        sb.Append($"    public const string SupportEmail = {Literal(b.SupportEmail)};\n");
        sb.Append($"    public const string Copyright = {Literal(b.Copyright)};\n");
        sb.Append($"    public const string PrimaryColor = {Literal(b.PrimaryColor)};\n");
        sb.Append($"    public const string AccentColor = {Literal(b.AccentColor)};\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    private static string RenderBrandCss(Brand b) =>
        $":root {{\n  --brand-primary: {b.PrimaryColor};\n  --brand-accent: {b.AccentColor};\n}}\n";

    private static string RenderWxl(Brand b)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<!-- Generated by VSoftSol.Syslog.BrandingGen from branding/brand.json. Do not edit. See BRANDING.md. -->\n");
        sb.Append("<WixLocalization xmlns=\"http://wixtoolset.org/schemas/v4/wxl\" Culture=\"en-us\">\n");
        sb.Append($"  <String Id=\"ProductName\" Value={XmlAttr(b.ProductName)} />\n");
        sb.Append($"  <String Id=\"Manufacturer\" Value={XmlAttr(b.VendorName)} />\n");
        sb.Append($"  <String Id=\"VendorUrl\" Value={XmlAttr(b.VendorUrl)} />\n");
        sb.Append($"  <String Id=\"SupportEmail\" Value={XmlAttr(b.SupportEmail)} />\n");
        sb.Append($"  <String Id=\"ServiceDescription\" Value={XmlAttr($"Collects, parses, and stores syslog messages, and hosts the HTTPS admin dashboard. Part of {b.ProductName}.")} />\n");
        sb.Append($"  <String Id=\"DotNetRequiredMessage\" Value={XmlAttr($"{b.ProductName} requires the ASP.NET Core Runtime 8.0.x (Hosting Bundle) to be installed first. Download it from https://dotnet.microsoft.com/download/dotnet/8.0 and run this installer again.")} />\n");
        sb.Append("</WixLocalization>\n");
        return sb.ToString();
    }

    private static string XmlAttr(string value) =>
        "\"" + value
            .Replace("&", "&amp;")
            .Replace("\"", "&quot;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;") + "\"";

    private static string Literal(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static Bitmap NewCanvas(int w, int h, PixelFormat format = PixelFormat.Format32bppArgb)
    {
        var bmp = new Bitmap(w, h, format);
        bmp.SetResolution(96, 96); // fixed, so the encoded bytes do not depend on the machine's DPI
        return bmp;
    }

    /// <summary>
    /// Decodes into a fresh 32bpp ARGB bitmap, so no file handle stays open and every source
    /// format (indexed, 24bpp, 16-bit) is normalised before it is resized.
    /// </summary>
    private static Bitmap LoadBitmap(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        using var decoded = new Bitmap(stream);
        Bitmap copy = NewCanvas(decoded.Width, decoded.Height);
        using Graphics g = Graphics.FromImage(copy);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(decoded, new Rectangle(0, 0, decoded.Width, decoded.Height));
        return copy;
    }

    private static Bitmap SolidPlaceholder(string hex)
    {
        Bitmap img = NewCanvas(512, 512);
        using Graphics g = Graphics.FromImage(img);
        g.Clear(ParseHex(hex));
        return img;
    }

    private static Bitmap Square(Bitmap master, int size) => Resized(master, size, size);

    private static Bitmap Letterbox(Bitmap master, int w, int h) => Resized(master, w, h);

    /// <summary>Scales to fit inside w×h, keeping the aspect ratio, centred on a transparent canvas.</summary>
    private static Bitmap Resized(Bitmap master, int w, int h)
    {
        Bitmap canvas = NewCanvas(w, h);
        using Graphics g = Graphics.FromImage(canvas);
        (int dw, int dh) = FitWithin(master, w, h);
        DrawScaled(g, master, new Rectangle((w - dw) / 2, (h - dh) / 2, dw, dh), CompositingMode.SourceCopy);
        return canvas;
    }

    private static (int Width, int Height) FitWithin(Bitmap source, int w, int h)
    {
        double scale = Math.Min((double)w / source.Width, (double)h / source.Height);
        return (Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)));
    }

    private static void DrawScaled(Graphics g, Bitmap source, Rectangle target, CompositingMode mode)
    {
        g.CompositingMode = mode;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;

        // Mirror edge pixels so the resampler does not blend a dark/transparent halo into the border.
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(source, target, 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
    }

    private static Bitmap Monochrome(Bitmap master, string hex)
    {
        Bitmap clone = NewCanvas(master.Width, master.Height);
        using (Graphics g = Graphics.FromImage(clone))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(master, new Rectangle(0, 0, master.Width, master.Height));
        }

        Color tint = ParseHex(hex);
        BitmapData data = clone.LockBits(
            new Rectangle(0, 0, clone.Width, clone.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[data.Width * 4];
            for (int y = 0; y < data.Height; y++)
            {
                IntPtr line = data.Scan0 + (y * data.Stride);
                Marshal.Copy(line, row, 0, row.Length);
                for (int x = 0; x < row.Length; x += 4)
                {
                    row[x] = tint.B; // BGRA in memory; alpha (x + 3) is kept
                    row[x + 1] = tint.G;
                    row[x + 2] = tint.R;
                }

                Marshal.Copy(row, 0, line, row.Length);
            }
        }
        finally
        {
            clone.UnlockBits(data);
        }

        return clone;
    }

    private static byte[] Encode(Bitmap image, ImageFormat format)
    {
        using var ms = new MemoryStream();
        image.Save(ms, format);
        return ms.ToArray();
    }

    private static byte[] EncodePng(Bitmap image) => Encode(image, ImageFormat.Png);

    private static void EmitPng(string path, Bitmap image, List<string> warnings)
    {
        using (image)
        {
            WriteBytesIfDifferent(path, EncodePng(image), warnings);
        }
    }

    private static void EmitOverridablePng(
        string brandingDir, string outPath, string name, Func<Bitmap> make, List<string> warnings)
    {
        string supplied = Path.Combine(brandingDir, name);
        if (File.Exists(supplied))
        {
            using Bitmap img = LoadBitmap(supplied);
            WriteBytesIfDifferent(outPath, EncodePng(img), warnings);
            return;
        }

        EmitPng(outPath, make(), warnings);
    }

    private static byte[] OverrideOrIco(string brandingDir, string name, Bitmap master, int[] sizes)
    {
        string supplied = Path.Combine(brandingDir, name);
        if (File.Exists(supplied))
        {
            return File.ReadAllBytes(supplied);
        }

        var frames = new List<byte[]>(sizes.Length);
        foreach (int s in sizes)
        {
            using Bitmap frame = Resized(master, s, s);
            frames.Add(EncodePng(frame));
        }

        return BuildIco(sizes, frames);
    }

    private static byte[] OverrideOrBmp(
        string brandingDir, string name, Bitmap master, int w, int h, string bg)
    {
        string supplied = Path.Combine(brandingDir, name);
        if (File.Exists(supplied))
        {
            return File.ReadAllBytes(supplied);
        }

        using Bitmap canvas = NewCanvas(w, h, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(canvas))
        {
            g.Clear(ParseHex(bg));
            int target = (int)(h * 0.8);
            (int lw, int lh) = FitWithin(master, target, target);
            DrawScaled(g, master, new Rectangle((h - lw) / 2, (h - lh) / 2, lw, lh), CompositingMode.SourceOver);
        }

        return Encode(canvas, ImageFormat.Bmp);
    }

    /// <summary>Wraps PNG frames in a minimal, valid ICONDIR container (PNG-in-ICO, Vista+).</summary>
    private static byte[] BuildIco(int[] sizes, List<byte[]> frames)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)frames.Count);

        int offset = 6 + (16 * frames.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            int dim = sizes[i];
            w.Write((byte)(dim >= 256 ? 0 : dim));
            w.Write((byte)(dim >= 256 ? 0 : dim));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(frames[i].Length);
            w.Write(offset);
            offset += frames[i].Length;
        }

        foreach (byte[] frame in frames)
        {
            w.Write(frame);
        }

        w.Flush();
        return ms.ToArray();
    }

    private static Color ParseHex(string hex)
    {
        string h = hex.TrimStart('#');
        byte r = byte.Parse(h.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte g = byte.Parse(h.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte b = byte.Parse(h.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Color.FromArgb(255, r, g, b);
    }

    private static void WriteTextIfDifferent(string path, string content, List<string> warnings) =>
        WriteBytesIfDifferent(path, new UTF8Encoding(false).GetBytes(content.Replace("\r\n", "\n")), warnings);

    private static void WriteBytesIfDifferent(string path, byte[] bytes, List<string> warnings)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            return;
        }

        try
        {
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not write {path}: {ex.Message}");
            throw;
        }
    }

    private readonly record struct Brand(
        string ProductName,
        string VendorName,
        string VendorUrl,
        string SupportEmail,
        string PrimaryColor,
        string AccentColor,
        string Copyright);
}
