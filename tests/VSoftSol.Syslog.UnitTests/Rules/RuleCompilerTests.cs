using FluentAssertions;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Rules.Rules;
using Xunit;
using static VSoftSol.Syslog.UnitTests.Rules.RuleTestBuilders;

namespace VSoftSol.Syslog.UnitTests.Rules;

public sealed class RuleCompilerTests
{
    private static readonly RuleCompiler Default = new();

    [Fact]
    public void Compile_ValidRule_Succeeds()
    {
        RuleCompileResult r = Default.Compile(Rule("email on crit", null, new SendEmailAction
        {
            Host = "smtp.example.com",
            From = "alerts@example.com",
            To = ["ops@example.com"],
            Subject = "{severity}: {message}",
            Body = "{message}",
        }));

        r.Success.Should().BeTrue(because: string.Join("; ", r.Errors));
    }

    [Fact]
    public void Compile_NoActions_Fails()
    {
        var rule = Rule("empty", null);
        rule.Actions.Clear();
        Default.Compile(rule).Errors.Should().Contain(e => e.Contains("no actions"));
    }

    [Theory]
    [InlineData("ftp://example.com/hook")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    public void Compile_WebhookWithNonHttpScheme_Fails(string url)
    {
        RuleCompileResult r = Default.Compile(Rule("wh", null, new HttpWebhookAction { Url = url }));
        r.Success.Should().BeFalse();
        r.Errors.Should().Contain(e => e.Contains("http", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compile_ScriptOutsideAllowList_Fails()
    {
        var compiler = new RuleCompiler(new RuleCompileOptions(
            ScriptAllowListDirectories: [OperatingSystem.IsWindows() ? @"C:\scripts" : "/opt/scripts"],
            FileActionBaseDirectory: null,
            LocalSyslogEndpoints: []));

        string outside = OperatingSystem.IsWindows() ? @"C:\Windows\System32\cmd.exe" : "/bin/sh";
        RuleCompileResult r = compiler.Compile(Rule("s", null, new RunScriptAction { ExecutablePath = outside }));

        r.Success.Should().BeFalse();
        r.Errors.Should().Contain(e => e.Contains("allow-listed"));
    }

    [Fact]
    public void Compile_ScriptWithRelativeOrTraversalPath_Fails()
    {
        RuleCompileResult r = Default.Compile(Rule("s", null, new RunScriptAction
        {
            ExecutablePath = OperatingSystem.IsWindows() ? @"C:\scripts\..\evil.exe" : "/opt/../bin/sh",
        }));
        r.Success.Should().BeFalse();
        r.Errors.Should().Contain(e => e.Contains("..") || e.Contains("absolute"));
    }

    [Fact]
    public void Compile_ForwardToOwnListener_IsRejectedAsALoop()
    {
        var compiler = new RuleCompiler(new RuleCompileOptions([], null, LocalSyslogEndpoints: ["127.0.0.1:514"]));
        RuleCompileResult r = compiler.Compile(Rule("f", null, new ForwardSyslogAction { Host = "127.0.0.1", Port = 514 }));
        r.Success.Should().BeFalse();
        r.Errors.Should().Contain(e => e.Contains("loop"));
    }

    [Fact]
    public void Compile_FileNameWithTraversal_Fails()
    {
        RuleCompileResult r = Default.Compile(Rule("wf", null, new WriteToFileAction
        {
            FileName = "../../etc/cron.d/evil",
            LineTemplate = "{message}",
        }));
        r.Success.Should().BeFalse();
    }

    [Fact]
    public void Compile_OdbcWithNonIdentifierTable_Fails()
    {
        RuleCompileResult r = Default.Compile(Rule("o", null, new WriteToOdbcAction
        {
            ConnectionString = "DSN=x",
            TableName = "events; DROP TABLE users;--",
            ColumnMap = { ["msg"] = "{message}" },
        }));
        r.Success.Should().BeFalse();
    }

    [Fact]
    public void Compile_TemplateWithUnknownField_Fails()
    {
        RuleCompileResult r = Default.Compile(Rule("n", null, new RaiseNotificationAction { Title = "{no_such_field}" }));
        r.Success.Should().BeFalse();
    }

    [Fact]
    public void Compile_EscalationWithoutActions_Fails()
    {
        var rule = Rule("esc", null, Notify());
        rule.Escalation = new EscalationPolicy { Threshold = 3, WindowSeconds = 60 };
        Default.Compile(rule).Errors.Should().Contain(e => e.Contains("no actions"));
    }
}
