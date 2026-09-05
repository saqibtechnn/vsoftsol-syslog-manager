using FluentAssertions;
using SixLabors.ImageSharp;
using VSoftSol.Syslog.BrandingGen;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Branding;

/// <summary>
/// PHASE_00 branding tests: assets derived at the right dimensions when a logo is
/// present; a warning + placeholder fallback + successful run when it is missing;
/// byte-identical output on a second run.
/// </summary>
public sealed class BrandingPipelineTests : IDisposable
{
    private readonly string _repo;

    public BrandingPipelineTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "vsoftsol-brand-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_repo, "branding", "placeholder"));
        File.Copy(
            Path.Combine(RepoPaths.Branding, "placeholder", "logo.png"),
            Path.Combine(_repo, "branding", "placeholder", "logo.png"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_repo, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private string Web(string name) =>
        Path.Combine(_repo, "src", "VSoftSol.Syslog.Web", "wwwroot", "branding", name);

    private string Installer(string name) => Path.Combine(_repo, "installer", "assets", name);

    private string GeneratedInfo() =>
        Path.Combine(_repo, "src", "VSoftSol.Syslog.Core", "Generated", "BrandingInfo.g.cs");

    private void WriteOperatorLogo() =>
        File.Copy(
            Path.Combine(_repo, "branding", "placeholder", "logo.png"),
            Path.Combine(_repo, "branding", "logo.png"));

    private void WriteBrandJson() => File.WriteAllText(
        Path.Combine(_repo, "branding", "brand.json"),
        """
        {
          "productName": "Acme Log Hub",
          "vendorName": "Acme Corp",
          "vendorUrl": "https://acme.example",
          "supportEmail": "help@acme.example",
          "primaryColor": "#123456",
          "accentColor": "#ABCDEF",
          "copyright": "(c) Acme"
        }
        """);

    [Fact]
    public void Generate_WithOperatorLogo_EmitsEveryDerivedAssetAtExpectedDimensions()
    {
        WriteOperatorLogo();

        BrandingResult result = BrandingGenerator.Generate(_repo);

        result.Warnings.Should().BeEmpty();
        File.Exists(GeneratedInfo()).Should().BeTrue();
        File.Exists(Web("brand.css")).Should().BeTrue();

        Dimensions(Web("logo.png")).Should().Be((512, 512));
        Dimensions(Web("logo-wide.png")).Should().Be((800, 200));
        Dimensions(Web("logo-mono.png")).Should().Be((512, 512));
        Dimensions(Installer("installer-banner.bmp")).Should().Be((493, 58));
        Dimensions(Installer("installer-dialog.bmp")).Should().Be((493, 312));

        IcoFrameSizes(Web("favicon.ico")).Should().BeEquivalentTo(new[] { 16, 32, 48 });
        IcoFrameSizes(Web("app-icon.ico")).Should().BeEquivalentTo(new[] { 16, 32, 48, 256 });
        File.Exists(Installer("app-icon.ico")).Should().BeTrue();
    }

    [Fact]
    public void Generate_WithMissingLogo_WarnsUsesPlaceholderAndStillSucceeds()
    {
        BrandingResult result = BrandingGenerator.Generate(_repo);

        result.Warnings.Should().ContainMatch("*logo.png*");
        File.Exists(Web("logo.png")).Should().BeTrue();
        Dimensions(Web("logo-wide.png")).Should().Be((800, 200));
    }

    [Fact]
    public void Generate_RunTwice_ProducesByteIdenticalOutput()
    {
        WriteOperatorLogo();
        WriteBrandJson();

        BrandingGenerator.Generate(_repo);
        Dictionary<string, byte[]> first = SnapshotOutputs();

        BrandingGenerator.Generate(_repo);
        Dictionary<string, byte[]> second = SnapshotOutputs();

        second.Keys.Should().BeEquivalentTo(first.Keys);
        foreach ((string path, byte[] bytes) in first)
        {
            second[path].Should().Equal(bytes, "output {0} must be reproducible", path);
        }
    }

    [Fact]
    public void Generate_GeneratedInfo_CarriesBrandValuesFromBrandJson()
    {
        WriteOperatorLogo();
        WriteBrandJson();

        BrandingGenerator.Generate(_repo);

        string generated = File.ReadAllText(GeneratedInfo());
        generated.Should().Contain("\"Acme Log Hub\"");
        generated.Should().Contain("\"Acme Corp\"");
        generated.Should().Contain("\"#123456\"");

        string css = File.ReadAllText(Web("brand.css"));
        css.Should().Contain("--brand-primary: #123456;");
        css.Should().Contain("--brand-accent: #ABCDEF;");
    }

    [Fact]
    public void Generate_WithNoBrandJson_FallsBackToDocumentedDefaults()
    {
        WriteOperatorLogo();

        BrandingGenerator.Generate(_repo);

        string generated = File.ReadAllText(GeneratedInfo());
        generated.Should().Contain("\"VSoftSol Syslog Manager\"");
        generated.Should().Contain("\"#0F4C81\"");
    }

    private Dictionary<string, byte[]> SnapshotOutputs()
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string root in new[]
        {
            Path.Combine(_repo, "src", "VSoftSol.Syslog.Web", "wwwroot", "branding"),
            Path.Combine(_repo, "installer", "assets"),
            Path.Combine(_repo, "src", "VSoftSol.Syslog.Core", "Generated"),
        })
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                result[Path.GetRelativePath(_repo, file)] = File.ReadAllBytes(file);
            }
        }

        return result;
    }

    private static (int Width, int Height) Dimensions(string path)
    {
        IImageInfo info = Image.Identify(path)
            ?? throw new InvalidOperationException($"Not a recognised image: {path}");
        return (info.Width, info.Height);
    }

    private static int[] IcoFrameSizes(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int count = BitConverter.ToUInt16(bytes, 4);
        var sizes = new int[count];
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + (i * 16);
            int w = bytes[entry];
            sizes[i] = w == 0 ? 256 : w;
        }

        return sizes;
    }
}
