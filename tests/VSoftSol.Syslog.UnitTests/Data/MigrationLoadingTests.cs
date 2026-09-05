using FluentAssertions;
using VSoftSol.Syslog.Data.Migrations;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Data;

public sealed class MigrationLoadingTests
{
    [Fact]
    public void LoadEmbeddedMigrations_ReturnsAGapFreeSequenceStartingAtOne()
    {
        IReadOnlyList<Migration> migrations = MigrationRunner.LoadEmbeddedMigrations();

        migrations.Should().NotBeEmpty();
        migrations.Select(m => m.Version).Should().BeInAscendingOrder();
        migrations.Select(m => m.Version).Should().Equal(Enumerable.Range(1, migrations.Count));
        migrations[0].Name.Should().Be("initial");
    }

    [Fact]
    public void Checksum_IsStable_AndInsensitiveToLineEndings()
    {
        const string body = "CREATE TABLE t (a INTEGER);\nCREATE INDEX ix ON t(a);\n";

        string lf = Migration.ComputeChecksum(body);
        string crlf = Migration.ComputeChecksum(body.Replace("\n", "\r\n"));

        lf.Should().HaveLength(64);
        lf.Should().MatchRegex("^[0-9a-f]{64}$");
        crlf.Should().Be(lf);
    }

    [Fact]
    public void Checksum_Changes_WhenTheScriptChanges()
    {
        Migration.ComputeChecksum("A").Should().NotBe(Migration.ComputeChecksum("B"));
    }

    [Fact]
    public void Migration_RejectsNonPositiveVersions()
    {
        Action act = () => _ = new Migration(0, "x", "SELECT 1;");

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
