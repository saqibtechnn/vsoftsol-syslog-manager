using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Data.Search;
using VSoftSol.Syslog.Ingestion.Extraction;
using VSoftSol.Syslog.Ingestion.Parsing;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Service.Hosting;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Hosting;

/// <summary>
/// v1.1 — P5-3 (`docs/evidence/phase-05/known-issues.md`): loads operator-saved extractors
/// (Settings → Pattern tester, `user_extractors`) at collector startup and hands the
/// compiled pipeline to <see cref="UserExtractorRegistry"/>, the same load-once,
/// restart-to-pick-up-changes disposition <see cref="VSoftSol.Syslog.Ingestion.Patterns.PatternPackLoader"/>
/// already has for vendor packs.
/// </summary>
[Trait("Category", "Hosting")]
public sealed class UserExtractorLoaderHostedServiceTests
{
    private static IOptions<ParsingOptions> DefaultParsing() => Options.Create(new ParsingOptions());

    [Fact]
    public async Task StartAsync_LoadsEnabledExtractors_IntoTheRegistry()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteExtractorStore(db.Factory);
        await store.SaveAsync("has-user", "grok", "user %{USERNAME:user} logged in", null, "alice", CancellationToken.None);
        var registry = new UserExtractorRegistry();

        var service = new UserExtractorLoaderHostedService(store, registry, DefaultParsing(), NullLogger<UserExtractorLoaderHostedService>.Instance);
        await service.StartAsync(CancellationToken.None);

        registry.Current.StageCount.Should().Be(1);

        var ctx = new ExtractionContext("user carol logged in", "10.0.0.1", null, null, 100, 4096);
        registry.Current.Run(ctx);
        ctx.Fields.Should().Contain(new KeyValuePair<string, string>("user", "carol"));
    }

    [Fact]
    public async Task StartAsync_SkipsDisabledExtractors()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteExtractorStore(db.Factory);
        await store.SaveAsync("enabled-one", "regex", @"(?<x>\d+)", null, "alice", CancellationToken.None);
        await using (var connection = await db.Factory.OpenAsync(CancellationToken.None))
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE user_extractors SET enabled = 0 WHERE name = 'enabled-one';";
            await cmd.ExecuteNonQueryAsync();
        }

        var registry = new UserExtractorRegistry();
        var service = new UserExtractorLoaderHostedService(store, registry, DefaultParsing(), NullLogger<UserExtractorLoaderHostedService>.Instance);
        await service.StartAsync(CancellationToken.None);

        registry.Current.StageCount.Should().Be(0, "the only saved extractor was disabled");
    }

    [Fact]
    public async Task StartAsync_MalformedSavedPattern_IsSkipped_ButOthersStillLoad()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteExtractorStore(db.Factory);
        // The store itself does not validate regex syntax (only the UI's live tester does),
        // so a bad pattern can still land in the table — this must never take the collector
        // down (same contract PatternPackLoader already gives malformed .pack files).
        await store.SaveAsync("broken", "regex", "(unterminated", null, "alice", CancellationToken.None);
        await store.SaveAsync("good", "grok", "%{INT:n}", null, "alice", CancellationToken.None);

        var registry = new UserExtractorRegistry();
        var service = new UserExtractorLoaderHostedService(store, registry, DefaultParsing(), NullLogger<UserExtractorLoaderHostedService>.Instance);
        await service.StartAsync(CancellationToken.None);

        registry.Current.StageCount.Should().Be(1, "the malformed extractor is skipped, not fatal");
    }

    [Fact]
    public async Task StartAsync_NoSavedExtractors_LeavesTheRegistryEmpty()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteExtractorStore(db.Factory);
        var registry = new UserExtractorRegistry();

        var service = new UserExtractorLoaderHostedService(store, registry, DefaultParsing(), NullLogger<UserExtractorLoaderHostedService>.Instance);
        await service.StartAsync(CancellationToken.None);

        registry.Current.StageCount.Should().Be(0);
    }
}
