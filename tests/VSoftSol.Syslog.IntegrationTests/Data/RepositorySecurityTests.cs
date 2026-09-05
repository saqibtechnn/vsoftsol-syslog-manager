using FluentAssertions;
using Microsoft.Extensions.Logging;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Data.Migrations;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Data;

public sealed class RepositorySecurityTests
{
    [Fact]
    public async Task Query_ScopedToAStreamThatHasNoEvents_ReturnsNothing_NotEverything()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        await db.Repository.AppendBatchAsync(
            Enumerable.Range(0, 25).Select(i => SampleEvents.Minimal($"e{i}")).ToList(), CancellationToken.None);

        long unfiltered = await db.Repository.CountAsync(new LogQuery(), CancellationToken.None);
        long scopedToEmpty = await db.Repository.CountAsync(
            new LogQuery { StreamIds = [424242] }, CancellationToken.None);
        long scopedToUnknownDevice = await db.Repository.CountAsync(
            new LogQuery { DeviceIds = [999999] }, CancellationToken.None);

        unfiltered.Should().Be(25);
        scopedToEmpty.Should().Be(0, "an unresolvable scope must fail closed");
        scopedToUnknownDevice.Should().Be(0, "an unresolvable scope must fail closed");
    }

    [Fact]
    public async Task RepositoryErrorPaths_DoNotWriteMessagePayloadsToTheLog()
    {
        const string secretPayload = "P@ssw0rd-in-a-syslog-body-should-never-be-logged";

        var sink = new CapturingLoggerProvider();
        using ILoggerFactory loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(sink);
        });

        await using SqliteTestDatabase db = SqliteTestDatabase.CreateUnmigrated();
        var runner = new MigrationRunner(db.Factory, loggerFactory.CreateLogger<MigrationRunner>());
        await runner.MigrateAsync(CancellationToken.None);

        var badEvent = new SyslogEvent
        {
            ReceivedUtc = DateTimeOffset.UtcNow,
            SourceIp = "10.0.0.9",
            Message = secretPayload,
            RawMessage = System.Text.Encoding.UTF8.GetBytes(secretPayload),
            Facility = Facility.Local0,
            Severity = Severity.Informational,
            Protocol = Protocol.Udp,
            ParseStatus = ParseStatus.Raw,
            ListenerId = 987654, // dangling FK -> constraint failure on write
        };

        Func<Task> act = () => db.Repository.AppendAsync(badEvent, CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();

        sink.AllText.Should().NotContain(secretPayload);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly System.Text.StringBuilder _buffer = new();

        public string AllText
        {
            get
            {
                lock (_buffer)
                {
                    return _buffer.ToString();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_buffer);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly System.Text.StringBuilder _buffer;

            public CapturingLogger(System.Text.StringBuilder buffer) => _buffer = buffer;

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (_buffer)
                {
                    _buffer.AppendLine(formatter(state, exception));
                    if (exception is not null)
                    {
                        _buffer.AppendLine(exception.ToString());
                    }
                }
            }
        }
    }
}
