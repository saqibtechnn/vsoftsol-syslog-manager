using FluentAssertions;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Hardening;

/// <summary>
/// Static files carry an ETag but no explicit freshness, so browsers cache them heuristically for
/// days. After an upgrade that changes markup and stylesheet together, an admin kept the OLD
/// ds.css against the NEW pages and saw a broken layout until a hard refresh. Static assets must
/// revalidate on every load (a 304 is cheap) so an upgrade shows up immediately.
/// </summary>
public sealed class StaticAssetCachingTests : IClassFixture<SyslogWebApplicationFactory>
{
    private readonly SyslogWebApplicationFactory _factory;

    public StaticAssetCachingTests(SyslogWebApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/ds.css")]
    [InlineData("/app.css")]
    [InlineData("/js/app.js")]
    public async Task StaticAssets_MustRevalidateOnEveryLoad(string path)
    {
        HttpResponseMessage response = await _factory.CreateClient().GetAsync(path);

        response.IsSuccessStatusCode.Should().BeTrue();
        response.Headers.CacheControl.Should().NotBeNull($"{path} must state its caching policy");
        response.Headers.CacheControl!.NoCache.Should().BeTrue($"{path} must revalidate (ETag/304) so an upgrade is picked up");
    }
}
