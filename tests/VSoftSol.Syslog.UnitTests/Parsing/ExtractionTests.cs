using FluentAssertions;
using VSoftSol.Syslog.Ingestion.Extraction;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Parsing;

[Trait("Category", "Parsing")]
public sealed class ExtractionTests
{
    private static ExtractionContext Ctx(string message) => new(message, "10.0.0.1", "host", "app", 250, 8192);

    [Fact]
    public void Grok_NamedCaptures_BecomeFields()
    {
        var grok = new GrokLibrary(TimeSpan.FromMilliseconds(250));
        var ex = new GrokExtractor(grok.Compile(@"user %{USERNAME:user} from %{IPV4:src_ip} port %{POSINT:src_port}"));
        var ctx = Ctx("user alice from 10.0.0.5 port 51234 done");

        ex.Apply(ctx);

        ctx.Fields.Should().Contain(new KeyValuePair<string, string>("user", "alice"));
        ctx.Fields.Should().Contain(new KeyValuePair<string, string>("src_ip", "10.0.0.5"));
        ctx.Fields.Should().Contain(new KeyValuePair<string, string>("src_port", "51234"));
    }

    [Fact]
    public void Grok_UnknownPattern_ThrowsAtCompileTime_NotAtMatchTime()
    {
        var grok = new GrokLibrary(TimeSpan.FromMilliseconds(250));
        Action act = () => grok.Compile("%{TOTALLY_UNKNOWN:x}");
        act.Should().Throw<GrokCompilationException>();
    }

    [Fact]
    public void Grok_RecursivePattern_IsRejected_NotHung()
    {
        var grok = new GrokLibrary(TimeSpan.FromMilliseconds(250), new Dictionary<string, string> { ["LOOP"] = "%{LOOP}" });
        Action act = () => grok.Compile("%{LOOP:x}");
        act.Should().Throw<GrokCompilationException>();
    }

    [Fact]
    public void KeyValue_ParsesQuotedAndBareValues_FortiGateStyle()
    {
        var ex = new KeyValueExtractor();
        var ctx = Ctx("date=2026-03-01 devname=\"fw one\" srcip=10.0.0.5 action=\"accept\"");

        ex.Apply(ctx);

        ctx.Fields["date"].Should().Be("2026-03-01");
        ctx.Fields["devname"].Should().Be("fw one");
        ctx.Fields["srcip"].Should().Be("10.0.0.5");
        ctx.Fields["action"].Should().Be("accept");
    }

    [Fact]
    public void Json_EmbeddedObject_IsFlattenedToDottedFields()
    {
        var ex = new JsonExtractor();
        var ctx = Ctx("prefix text {\"event\":{\"type\":\"login\",\"user\":\"bob\"},\"ok\":true}");

        ex.Apply(ctx);

        ctx.Fields["event.type"].Should().Be("login");
        ctx.Fields["event.user"].Should().Be("bob");
        ctx.Fields["ok"].Should().Be("true");
    }

    [Fact]
    public void Json_DeeplyNested_IsDepthLimited_NotStackOverflow()
    {
        string deep = string.Concat(Enumerable.Repeat("{\"a\":", 5000)) + "1" + new string('}', 5000);
        var ex = new JsonExtractor(maxDepth: 8);
        var ctx = Ctx(deep);

        Action act = () => ex.Apply(ctx);

        act.Should().NotThrow();
    }

    [Fact]
    public void Csv_PositionalColumns_WithQuotedComma_AreAssigned()
    {
        var ex = new CsvExtractor([null, "a", "b", "c"]);
        var ctx = Ctx("skip,one,\"two, still two\",three");

        ex.Apply(ctx);

        ctx.Fields["a"].Should().Be("one");
        ctx.Fields["b"].Should().Be("two, still two");
        ctx.Fields["c"].Should().Be("three");
    }

    [Fact]
    public void Csv_WhenGuard_OnlyAppliesForTheMatchingColumnValue()
    {
        var traffic = new CsvExtractor(["ignore", "x"], minFields: 2, whenColumn: 0, whenValue: "TRAFFIC");
        var matching = Ctx("TRAFFIC,captured");
        var other = Ctx("THREAT,notcaptured");

        traffic.Apply(matching);
        traffic.Apply(other);

        matching.Fields.Should().ContainKey("x");
        other.Fields.Should().NotContainKey("x");
    }

    [Fact]
    public void Lookup_MapsSourceValueThroughTable()
    {
        var ctx = Ctx("");
        ctx.Set("sev", "3");
        var ex = new LookupExtractor("sev", "sev_name", new Dictionary<string, string> { ["3"] = "error" });

        ex.Apply(ctx);

        ctx.Fields["sev_name"].Should().Be("error");
    }

    [Fact]
    public void Transform_RenameDropSet_AllApply()
    {
        var ctx = Ctx("");
        ctx.Set("old", "v");
        ctx.Set("gone", "x");
        var ex = new TransformExtractor([
            new TransformExtractor.Op(TransformExtractor.TransformKind.Rename, "old", "new"),
            new TransformExtractor.Op(TransformExtractor.TransformKind.Drop, "gone", ""),
            new TransformExtractor.Op(TransformExtractor.TransformKind.Set, "const", "1"),
        ]);

        ex.Apply(ctx);

        ctx.Fields.Should().NotContainKey("old").And.ContainKey("new");
        ctx.Fields.Should().NotContainKey("gone");
        ctx.Fields["const"].Should().Be("1");
    }

    [Fact]
    public void Context_FieldCap_IsEnforced_AndReported()
    {
        var ctx = new ExtractionContext("m", "1.1.1.1", null, null, maxFields: 3, maxValueLength: 100);
        for (int i = 0; i < 10; i++)
        {
            ctx.Set("f" + i, "v");
        }

        ctx.Fields.Should().HaveCount(3);
        ctx.FieldCapHit.Should().BeTrue();
    }

    [Fact]
    public void Context_LongValue_IsClamped()
    {
        var ctx = new ExtractionContext("m", "1.1.1.1", null, null, 250, maxValueLength: 16);
        ctx.Set("big", new string('x', 500));
        ctx.Fields["big"].Should().HaveLength(16);
    }

    [Fact]
    public void Pipeline_OneFailingStage_DoesNotStopTheOthers()
    {
        var pipeline = new ExtractorPipeline([new ThrowingExtractor(), new ConstExtractor()]);
        var ctx = Ctx("x");

        pipeline.Run(ctx);

        ctx.Fields.Should().ContainKey("const_ran");
        ctx.Fields.Should().ContainKey("extractor_error");
    }

    private sealed class ThrowingExtractor : IExtractor
    {
        public string Kind => "boom";

        public void Apply(ExtractionContext context) => throw new InvalidOperationException("stage failure");
    }

    private sealed class ConstExtractor : IExtractor
    {
        public string Kind => "const";

        public void Apply(ExtractionContext context) => context.Set("const_ran", "yes");
    }
}
