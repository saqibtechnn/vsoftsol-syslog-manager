using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Alerts;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Rules.Actions;
using VSoftSol.Syslog.Rules.Alerts;
using VSoftSol.Syslog.Service.Hosting;

namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

/// <summary>One notification raised during a test.</summary>
internal sealed record RecordedNotification(NotificationLevel Level, string Title, string Body, long? RuleId);

/// <summary>
/// Wires the Phase 8 alert scheduler + its action dispatcher over a test database on a
/// virtual clock (TESTING_STANDARDS §2.2 — no <c>Thread.Sleep</c>). Every component shares
/// one <see cref="FakeTimeProvider"/> and one <see cref="SqliteAlertStore"/> so the version
/// counter and the checkpoint behave as in production.
/// </summary>
internal sealed class AlertEvaluationHarness : IAsyncDisposable
{
    private readonly bool _ownsDb;

    private AlertEvaluationHarness(
        bool ownsDb,
        SqliteTestDatabase db,
        FakeTimeProvider clock,
        SqliteAlertStore store,
        SqliteAlertInstanceStore instances,
        SqliteAlertActionOutbox outbox,
        SqliteAlertWindowReader reader,
        SqliteAuditLog audit,
        AlertRuntime runtime,
        AlertEvaluationService evaluator,
        AlertActionDispatchService dispatcher,
        ConcurrentQueue<RecordedNotification> notifications)
    {
        _ownsDb = ownsDb;
        Db = db;
        Clock = clock;
        Store = store;
        Instances = instances;
        Outbox = outbox;
        Reader = reader;
        Audit = audit;
        Runtime = runtime;
        Evaluator = evaluator;
        Dispatcher = dispatcher;
        Notifications = notifications;
    }

    public SqliteTestDatabase Db { get; }

    public FakeTimeProvider Clock { get; }

    public SqliteAlertStore Store { get; }

    public SqliteAlertInstanceStore Instances { get; }

    public SqliteAlertActionOutbox Outbox { get; }

    public SqliteAlertWindowReader Reader { get; }

    public SqliteAuditLog Audit { get; }

    public AlertRuntime Runtime { get; }

    public AlertEvaluationService Evaluator { get; }

    public AlertActionDispatchService Dispatcher { get; }

    public ConcurrentQueue<RecordedNotification> Notifications { get; }

    public static async Task<AlertEvaluationHarness> CreateAsync(
        DateTimeOffset? start = null,
        Action<AlertEvaluationOptions>? configureEval = null,
        Action<AlertRuntimeOptions>? configureRuntime = null,
        SecretResolver? secrets = null)
    {
        SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        return Build(db, ownsDb: true, start ?? DateTimeOffset.UtcNow, configureEval, configureRuntime, secrets);
    }

    /// <summary>
    /// Simulates a process restart: fresh stores, provider, runtime, and clock over the
    /// <b>same</b> database file. The returned harness does not own disposal.
    /// </summary>
    public static AlertEvaluationHarness Reopen(
        AlertEvaluationHarness previous,
        DateTimeOffset at,
        Action<AlertEvaluationOptions>? configureEval = null,
        Action<AlertRuntimeOptions>? configureRuntime = null)
        => Build(previous.Db, ownsDb: false, at, configureEval, configureRuntime, secrets: null);

    private static AlertEvaluationHarness Build(
        SqliteTestDatabase db,
        bool ownsDb,
        DateTimeOffset start,
        Action<AlertEvaluationOptions>? configureEval,
        Action<AlertRuntimeOptions>? configureRuntime,
        SecretResolver? secrets)
    {
        var clock = new FakeTimeProvider(start);

        var store = new SqliteAlertStore(db.Factory, clock);
        var instances = new SqliteAlertInstanceStore(db.Factory, clock);
        var outbox = new SqliteAlertActionOutbox(db.Factory, clock);
        var reader = new SqliteAlertWindowReader(db.Factory);
        var audit = new SqliteAuditLog(db.Factory, clock);

        var evalOptions = new AlertEvaluationOptions();
        configureEval?.Invoke(evalOptions);
        var runtimeOptions = new AlertRuntimeOptions();
        configureRuntime?.Invoke(runtimeOptions);

        var runtime = new AlertRuntime(clock, runtimeOptions);
        var provider = new AlertSetProvider(store, NullLogger<AlertSetProvider>.Instance);
        var executors = new ActionExecutorRegistry();

        var notifications = new ConcurrentQueue<RecordedNotification>();
        NotificationSink sink = (level, title, body, ruleId, _) =>
        {
            notifications.Enqueue(new RecordedNotification(level, title, body, ruleId));
            return ValueTask.CompletedTask;
        };

        SecretResolver secretResolver = secrets ?? ((_, _) => new ValueTask<string?>((string?)null));

        var evaluator = new AlertEvaluationService(
            provider, store, instances, reader, outbox, audit, runtime, sink,
            Options.Create(evalOptions), clock, NullLogger<AlertEvaluationService>.Instance);

        var dispatcher = new AlertActionDispatchService(
            outbox, store, instances, audit, executors, secretResolver, sink,
            Options.Create(new ActionExecutorOptions()), Options.Create(evalOptions), clock,
            NullLogger<AlertActionDispatchService>.Instance);

        return new AlertEvaluationHarness(ownsDb, db, clock, store, instances, outbox, reader, audit, runtime, evaluator, dispatcher, notifications);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsDb)
        {
            await Db.DisposeAsync();
        }
    }

    public Task TickAsync() => Evaluator.TickAsync(CancellationToken.None);

    public Task<int> DispatchAsync() => Dispatcher.PassAsync(CancellationToken.None);

    /// <summary>Advances the clock and runs one evaluation tick.</summary>
    public async Task AdvanceAndTickAsync(TimeSpan by)
    {
        Clock.Advance(by);
        await TickAsync();
    }

    public async Task<IReadOnlyList<VSoftSol.Syslog.Core.Alerts.AlertInstance>> OpenInstancesAsync() =>
        await Instances.ListOpenAsync(CancellationToken.None);

    public async Task<long> AuditCountAsync(string action)
    {
        await using var c = await Db.Factory.OpenAsync(CancellationToken.None);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM audit_log WHERE action = $a;";
        cmd.Parameters.AddWithValue("$a", action);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
