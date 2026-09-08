using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Streams;
using VSoftSol.Syslog.Rules.Conditions;
using VSoftSol.Syslog.Rules.Streams;
using VSoftSol.Syslog.Service.Hosting;
using VSoftSol.Syslog.Web.Security;

namespace VSoftSol.Syslog.Web.Streams;

public sealed record StreamActionResult(bool Ok, string Message, IReadOnlyList<string> Errors)
{
    public static StreamActionResult Success(string message) => new(true, message, []);

    public static StreamActionResult Fail(string message, IReadOnlyList<string>? errors = null) =>
        new(false, message, errors ?? []);
}

/// <summary>
/// Stream CRUD from the UI (PHASE_06 build item 6) and the stream rule tester (item 8).
/// Match rules are validated with the same <see cref="ConditionCompiler"/> the ingest
/// router uses, so "it saved" means "it will route". Scope is honoured: a stream-restricted
/// user sees and edits only their streams. Every mutation is audited.
/// </summary>
public sealed class StreamAdminService
{
    private readonly SqliteStreamStore _streams;
    private readonly StreamRouterProvider _routers;
    private readonly SqliteAuditLog _audit;
    private readonly CurrentUserAccessor _users;

    public StreamAdminService(
        SqliteStreamStore streams, StreamRouterProvider routers, SqliteAuditLog audit, CurrentUserAccessor users)
    {
        _streams = streams;
        _routers = routers;
        _audit = audit;
        _users = users;
    }

    public async Task<IReadOnlyList<StreamRow>> ListAsync(CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        return await _streams.ListAsync(user.Scope, ct).ConfigureAwait(false);
    }

    public async Task<StreamRow?> GetAsync(long id, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        if (!user.Scope.AllStreams && !user.Scope.StreamIds.Contains(id))
        {
            return null; // out of scope — no existence oracle
        }

        return await _streams.GetAsync(id, ct).ConfigureAwait(false);
    }

    public async Task<StreamActionResult> SaveAsync(
        long streamId, string name, string? description, bool enabled, ConditionNode? match, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        if (user.Role is not (Role.Administrator or Role.Operator))
        {
            return StreamActionResult.Fail("You do not have permission to change streams.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return StreamActionResult.Fail("Give the stream a name.");
        }

        if (streamId != 0 && await GetAsync(streamId, ct).ConfigureAwait(false) is null)
        {
            return StreamActionResult.Fail("That stream is not available to you.");
        }

        ConditionCompileResult compiled = new ConditionCompiler().Compile(match);
        if (!compiled.Success)
        {
            return StreamActionResult.Fail("The match rule has a problem.", compiled.Errors);
        }

        if (streamId == 0)
        {
            long id = await _streams.CreateAsync(name, description, match, user.UserName, ct).ConfigureAwait(false);
            await _audit.AppendAsync(new AuditEntry(AuditActions.StreamCreate, user.UserName, "stream",
                id.ToString(System.Globalization.CultureInfo.InvariantCulture), Detail: name), CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            if (!await _streams.UpdateAsync(streamId, name, description, enabled, match, user.UserName, ct).ConfigureAwait(false))
            {
                return StreamActionResult.Fail("That stream no longer exists.");
            }

            await _audit.AppendAsync(new AuditEntry(AuditActions.StreamUpdate, user.UserName, "stream",
                streamId.ToString(System.Globalization.CultureInfo.InvariantCulture), Detail: name), CancellationToken.None).ConfigureAwait(false);
        }

        return StreamActionResult.Success("Stream saved.");
    }

    public async Task<StreamActionResult> DeleteAsync(long streamId, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        if (user.Role is not (Role.Administrator or Role.Operator))
        {
            return StreamActionResult.Fail("You do not have permission to delete streams.");
        }

        if (await GetAsync(streamId, ct).ConfigureAwait(false) is not { IsSystem: false })
        {
            return StreamActionResult.Fail("That stream cannot be deleted.");
        }

        if (!await _streams.DeleteAsync(streamId, ct).ConfigureAwait(false))
        {
            return StreamActionResult.Fail("That stream cannot be deleted.");
        }

        await _audit.AppendAsync(new AuditEntry(AuditActions.StreamDelete, user.UserName, "stream",
            streamId.ToString(System.Globalization.CultureInfo.InvariantCulture)), CancellationToken.None).ConfigureAwait(false);
        return StreamActionResult.Success("Stream deleted.");
    }

    /// <summary>PHASE_06 item 8 — which streams would this message match, and why.</summary>
    public async Task<IReadOnlyList<StreamMatchExplanation>> TestAsync(SyslogEvent sample, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        IReadOnlyList<StreamRow> rows = await _streams.ListAsync(user.Scope, ct).ConfigureAwait(false);
        StreamRouter router = await _routers.GetAsync(ct).ConfigureAwait(false);
        HashSet<long> matched = router.Route(sample).ToHashSet();

        return rows
            .Where(r => r.Enabled)
            .Select(r => new StreamMatchExplanation(
                r.StreamId, r.Name, matched.Contains(r.StreamId),
                r.IsCatchAll ? "catch-all — every message" : Explain(r.Match, sample)))
            .ToList();
    }

    private static string Explain(ConditionNode? match, SyslogEvent e)
    {
        if (match is null)
        {
            return "no rule";
        }

        ConditionCompileResult compiled = new ConditionCompiler().Compile(match);
        if (!compiled.Success)
        {
            return "rule does not compile: " + string.Join("; ", compiled.Errors);
        }

        return ConditionEvaluator.Matches(compiled.Condition!, e) ? "matched the rule" : "did not match the rule";
    }
}

public sealed record StreamMatchExplanation(long StreamId, string Name, bool Matched, string Reason);
