using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace VSoftSol.Syslog.BrandingGen;

/// <summary>Outcome of a generation run.</summary>
public sealed record BrandingResult(IReadOnlyList<string> Warnings, string GeneratedInfoPath);

/// <summary>
/// Reads <c>branding/logo.png</c> and <c>branding/brand.json</c> and derives every
/// product asset the operator did not supply, per BRANDING.md. A missing
/// <c>logo.png</c> produces a warning and a placeholder fallback, never a failure.
/// Every write is content-compared first, so two runs produce byte-identical output.
/// </summary>
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

        Directory.CreateDirectory(coreGenDir);
        Directory.CreateDirectory(webDir);
        Directory.CreateDirectory(installerDir);

        Brand brand = LoadBrand(Path.Combine(brandingDir, "brand.json"));

        string infoPath = Path.Combine(coreGenDir, "BrandingInfo.g.cs");
        WriteTextIfDifferent(infoPath, RenderBrandingInfo(brand), warnings);
        WriteTextIfDifferent(Path.Combine(webDir, "brand.css"), RenderBrandCss(brand), warnings);

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

        using Image<Rgba32> master = sourceLogo.Length > 0
            ? Image.Load<Rgba32>(sourceLogo)
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

    private static string Literal(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static Image<Rgba32> SolidPlaceholder(string hex)
    {
        var img = new Image<Rgba32>(512, 512);
        img.Mutate(x => x.BackgroundColor(new Color(ParseHex(hex))));
        return img;
    }

    private static Image<Rgba32> Square(Image<Rgba32> master, int size) => Resized(master, size, size);

    private static Image<Rgba32> Letterbox(Image<Rgba32> master, int w, int h) => Resized(master, w, h);

    private static Image<Rgba32> Resized(Image<Rgba32> master, int w, int h)
    {
        Image<Rgba32> clone = master.Clone();
        clone.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(w, h),
            Mode = ResizeMode.Pad,
            PadColor = Color.Transparent,
            Sampler = KnownResamplers.Lanczos3,
        }));
        return clone;
    }

    private static Image<Rgba32> Monochrome(Image<Rgba32> master, string hex)
    {
        Image<Rgba32> clone = master.Clone();
        Rgba32 tint = ParseHex(hex);
        clone.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = new Rgba32(tint.R, tint.G, tint.B, row[x].A);
                }
            }
        });
        return clone;
    }

    private static byte[] EncodePng(Image<Rgba32> image)
    {
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder
        {
            ColorType = PngColorType.RgbWithAlpha,
            CompressionLevel = PngCompressionLevel.BestCompression,
        });
        return ms.ToArray();
    }

    private static byte[] EncodeBmp(Image<Rgba32> image)
    {
        using var ms = new MemoryStream();
        image.Save(ms, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel24 });
        return ms.ToArray();
    }

    private static void EmitPng(string path, Image<Rgba32> image, List<string> warnings)
    {
        using (image)
        {
            WriteBytesIfDifferent(path, EncodePng(image), warnings);
        }
    }

    private static void EmitOverridablePng(
        string brandingDir, string outPath, string name, Func<Image<Rgba32>> make, List<string> warnings)
    {
        string supplied = Path.Combine(brandingDir, name);
        if (File.Exists(supplied))
        {
            using Image<Rgba32> img = Image.Load<Rgba32>(supplied);
            WriteBytesIfDifferent(outPath, EncodePng(img), warnings);
            return;
        }

        EmitPng(outPath, make(), warnings);
    }

    private static byte[] OverrideOrIco(string brandingDir, string name, Image<Rgba32> master, int[] sizes)
    {
        string supplied = Path.Combine(brandingDir, name);
        if (File.Exists(supplied))
        {
            return File.ReadAllBytes(supplied);
        }

        var frames = new List<byte[]>(sizes.Length);
        foreach (int s in sizes)
        {
            using Image<Rgba32> frame = Resized(master, s, s);
            frames.Add(EncodePng(frame));
        }

        return BuildIco(sizes, frames);
    }

    private static byte[] OverrideOrBmp(
        string brandingDir, string name, Image<Rgba32> master, int w, int h, string bg)
    {
        string supplied = Path.Combine(brandingDir, name);
        if (File.Exists(supplied))
        {
            return File.ReadAllBytes(supplied);
        }

        using var canvas = new Image<Rgba32>(w, h);
        canvas.Mutate(x => x.BackgroundColor(new Color(ParseHex(bg))));

        int target = (int)(h * 0.8);
        using Image<Rgba32> logo = master.Clone();
        logo.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(target, target),
            Mode = ResizeMode.Max,
            Sampler = KnownResamplers.Lanczos3,
        }));
        var at = new Point((h - logo.Width) / 2, (h - logo.Height) / 2);
        canvas.Mutate(x => x.DrawImage(logo, at, 1f));
        return EncodeBmp(canvas);
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

    private static Rgba32 ParseHex(string hex)
    {
        string h = hex.TrimStart('#');
        byte r = byte.Parse(h.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte g = byte.Parse(h.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte b = byte.Parse(h.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new Rgba32(r, g, b, 255);
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
