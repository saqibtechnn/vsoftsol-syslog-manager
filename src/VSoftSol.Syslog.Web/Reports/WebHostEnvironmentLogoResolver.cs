using Microsoft.AspNetCore.Hosting;

namespace VSoftSol.Syslog.Web.Reports;

/// <summary>Reads the branding pipeline's derived wide logo from <c>wwwroot/branding</c>.
/// Never throws — a missing logo means the report renders without one, same as
/// BRANDING.md's build-time fallback discipline ("never fail the build because a logo is
/// missing").</summary>
public sealed class WebHostEnvironmentLogoResolver : IWebHostEnvironmentLogoResolver
{
    private readonly IWebHostEnvironment _environment;

    public WebHostEnvironmentLogoResolver(IWebHostEnvironment environment) => _environment = environment;

    public async Task<byte[]?> TryReadLogoWideAsync(CancellationToken cancellationToken)
    {
        try
        {
            string path = Path.Combine(_environment.WebRootPath, "branding", "logo-wide.png");
            return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
