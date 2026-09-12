using FluentAssertions;
using VSoftSol.Syslog.Ingestion.Extraction;
using VSoftSol.Syslog.Ingestion.Parsing;
using VSoftSol.Syslog.Ingestion.Patterns;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Parsing;

/// <summary>
/// v1.1 — P5-3: operator-saved extractors (Settings → Pattern tester) are wired into
/// <see cref="VendorExtractor.Enrich"/> as a global stage that always runs, on top of
/// whichever vendor pack (if any) matched.
/// </summary>
[Trait("Category", "Parsing")]
public sealed class VendorExtractorTests
{
    private static readonly ParsingOptions Options = new() { MaxFieldsPerMessage = 250, MaxFieldValueLength = 8192 };
    private static readonly GrokLibrary Grok = new(TimeSpan.FromMilliseconds(250));

    private static PatternPack MakePack(string vendor, string appNamePattern, string grokExpr)
    {
        return new PatternPack
        {
            Vendor = vendor,
            Priority = 100,
            SourcePath = $"{vendor}.pack",
            MatchRules = [new PatternPack.MatchRule(PatternPack.MatchField.AppName, new System.Text.RegularExpressions.Regex(appNamePattern))],
            Pipeline = new ExtractorPipeline([new GrokExtractor(Grok.Compile(grokExpr))]),
        };
    }

    [Fact]
    public void Enrich_NoVendorMatchAndNoUserExtractors_ReturnsEmptyResult()
    {
        var vendor = new VendorExtractor([], Options);

        VendorExtractor.Result result = vendor.Enrich(SyslogParseResult.Raw("nothing matches this"), "10.0.0.1");

        result.Vendor.Should().BeNull();
        result.Fields.Should().BeEmpty();
    }

    [Fact]
    public void Enrich_NoVendorMatch_ButUserExtractorMatches_ExtractsFieldsWithNullVendor()
    {
        var registry = new UserExtractorRegistry();
        registry.SetPipeline(new ExtractorPipeline([new GrokExtractor(Grok.Compile(@"user %{USERNAME:user} failed"))]));
        var vendor = new VendorExtractor([], Options, registry);

        VendorExtractor.Result result = vendor.Enrich(SyslogParseResult.Raw("user bob failed"), "10.0.0.1");

        result.Vendor.Should().BeNull("no built-in pack exists for this device — this is exactly the user-extensible path");
        result.Fields.Should().Contain(f => f.Name == "user" && f.Value == "bob");
    }

    [Fact]
    public void Enrich_VendorMatches_UserExtractorAlsoRuns_FieldsFromBothAreMerged()
    {
        var pack = MakePack("acme", "^acme$", @"code=%{INT:code}");
        var registry = new UserExtractorRegistry();
        registry.SetPipeline(new ExtractorPipeline([new GrokExtractor(Grok.Compile(@"extra=%{WORD:extra}"))]));
        var vendor = new VendorExtractor([pack], Options, registry);

        SyslogParseResult parse = SyslogParseResult.Raw("code=42 extra=hello") with { AppName = "acme" };
        VendorExtractor.Result result = vendor.Enrich(parse, "10.0.0.1");

        result.Vendor.Should().Be("acme");
        result.Fields.Should().Contain(f => f.Name == "code" && f.Value == "42");
        result.Fields.Should().Contain(f => f.Name == "extra" && f.Value == "hello");
    }

    [Fact]
    public void Enrich_UserExtractorFieldCollidesWithPackField_UserValueWinsBecauseItRanLast()
    {
        var pack = MakePack("acme", "^acme$", @"status=%{WORD:status}");
        var registry = new UserExtractorRegistry();
        registry.SetPipeline(new ExtractorPipeline([new GrokExtractor(Grok.Compile(@"status=(?<status>override)"))]));
        var vendor = new VendorExtractor([pack], Options, registry);

        SyslogParseResult parse = SyslogParseResult.Raw("status=override") with { AppName = "acme" };
        VendorExtractor.Result result = vendor.Enrich(parse, "10.0.0.1");

        result.Fields.Should().ContainSingle(f => f.Name == "status").Which.Value.Should().Be("override");
    }

    [Fact]
    public void UserExtractorRegistry_DefaultsToEmptyPipeline()
    {
        var registry = new UserExtractorRegistry();

        registry.Current.StageCount.Should().Be(0);
    }

    [Fact]
    public void UserExtractorRegistry_SetPipeline_ReplacesTheCurrentSnapshot()
    {
        var registry = new UserExtractorRegistry();
        var pipeline = new ExtractorPipeline([new GrokExtractor(Grok.Compile(@"%{WORD:w}"))]);

        registry.SetPipeline(pipeline);

        registry.Current.Should().BeSameAs(pipeline);
    }
}
