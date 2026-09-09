using FluentAssertions;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Rules.Templating;
using VSoftSol.Syslog.UnitTests.Conditions;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Rules;

public sealed class FieldTemplateTests
{
    [Fact]
    public void Render_SubstitutesKnownFields()
    {
        SyslogEvent e = ConditionTestBuilders.Event(message: "disk full", hostname: "fw-1");
        FieldTemplate.Render("[{hostname}] {message}", e).Should().Be("[fw-1] disk full");
    }

    [Fact]
    public void Render_UnknownToken_RendersEmpty_NeverThrows()
    {
        SyslogEvent e = ConditionTestBuilders.Event();
        FieldTemplate.Render("x={nope}y", e).Should().Be("x=y");
    }

    [Fact]
    public void Render_EscapedBraces_AreLiteral()
    {
        SyslogEvent e = ConditionTestBuilders.Event();
        FieldTemplate.Render("{{not a field}}", e).Should().Be("{not a field}");
    }

    [Theory]
    [InlineData("Subject\r\nBcc: evil@example.com")]
    [InlineData("line1\nline2")]
    [InlineData("tab\there")]
    public void Render_StripsControlCharsFromSubstitutedValues_ButKeepsTabs(string hostile)
    {
        SyslogEvent e = ConditionTestBuilders.Event(hostname: hostile);
        string rendered = FieldTemplate.Render("h={hostname}", e);
        rendered.Should().NotContain("\r").And.NotContain("\n");
        if (hostile.Contains('\t'))
        {
            rendered.Should().Contain("\t");
        }
    }

    [Fact]
    public void Render_CapsOutputAt64Kb()
    {
        SyslogEvent e = ConditionTestBuilders.Event(message: new string('a', 200_000));
        FieldTemplate.Render("{message}", e).Length.Should().BeLessThanOrEqualTo(64 * 1024);
    }

    [Fact]
    public void TryValidate_RejectsUnknownFieldAndUnbalancedBraces()
    {
        FieldTemplate.TryValidate("{message}", out _).Should().BeTrue();
        FieldTemplate.TryValidate("{bogus}", out string? e1).Should().BeFalse();
        e1.Should().Contain("bogus");
        FieldTemplate.TryValidate("{message", out _).Should().BeFalse();
    }
}
