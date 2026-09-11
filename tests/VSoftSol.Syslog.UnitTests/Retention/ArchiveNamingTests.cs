using FluentAssertions;
using VSoftSol.Syslog.Core.Retention;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Retention;

/// <summary>PHASE_10 — archive filename construction and path-traversal defence
/// (SECURITY_STANDARDS.md "Path traversal | ... may reach file paths in ... archive naming").</summary>
public sealed class ArchiveNamingTests
{
    [Theory]
    [InlineData("Firewall Logs", "Firewall Logs")]
    [InlineData("../../etc/passwd", "_.._etc_passwd")]
    [InlineData("C:\\Windows\\System32", "C__Windows_System32")]
    [InlineData("stream:with*bad?chars\"<>|", "stream_with_bad_chars____")]
    [InlineData("   ", "unnamed")]
    [InlineData("", "unnamed")]
    [InlineData(null, "unnamed")]
    public void SanitizeSegment_RemovesFilesystemUnsafeCharacters(string? input, string expected)
    {
        ArchiveNaming.SanitizeSegment(input).Should().Be(expected);
    }

    [Fact]
    public void SanitizeSegment_StripsLeadingDots_SoTraversalCannotSurvive()
    {
        ArchiveNaming.SanitizeSegment("..hidden").Should().NotStartWith(".");
    }

    [Fact]
    public void SanitizeSegment_CapsLength()
    {
        string huge = new('a', 500);
        ArchiveNaming.SanitizeSegment(huge).Length.Should().BeLessThanOrEqualTo(80);
    }

    [Fact]
    public void BuildFileName_IsDeterministic_ForTheSameStreamAndPeriod()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddDays(1);

        string a = ArchiveNaming.BuildFileName("Firewall Logs", start, end);
        string b = ArchiveNaming.BuildFileName("Firewall Logs", start, end);

        a.Should().Be(b);
        a.Should().EndWith(".vsarc");
    }

    [Fact]
    public void BuildFileName_ForDifferentPeriods_ProducesDifferentNames()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        string a = ArchiveNaming.BuildFileName("s", start, start.AddDays(1));
        string b = ArchiveNaming.BuildFileName("s", start, start.AddDays(2));

        a.Should().NotBe(b);
    }

    [Fact]
    public void BuildFileName_NeverContainsPathSeparators()
    {
        string name = ArchiveNaming.BuildFileName("../../evil", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
        name.Should().NotContain("/").And.NotContain("\\");
    }

    [Theory]
    [InlineData(@"C:\archives", @"C:\archives\stream_20260101.vsarc", true)]
    [InlineData(@"C:\archives", @"C:\archives\sub\stream_20260101.vsarc", true)]
    [InlineData(@"C:\archives", @"C:\other\stream_20260101.vsarc", false)]
    [InlineData(@"C:\archives", @"C:\archives-evil\stream.vsarc", false)]
    [InlineData(@"C:\archives", @"C:\archives", false)] // the root itself is not "under" the root
    public void IsSafeUnderRoot_DetectsTraversalOutsideTheConfiguredRoot(string root, string candidate, bool expected)
    {
        ArchiveNaming.IsSafeUnderRoot(root, candidate).Should().Be(expected);
    }

    [Fact]
    public void IsSafeUnderRoot_IsCaseInsensitive()
    {
        ArchiveNaming.IsSafeUnderRoot(@"C:\Archives", @"c:\archives\file.vsarc").Should().BeTrue();
    }
}
