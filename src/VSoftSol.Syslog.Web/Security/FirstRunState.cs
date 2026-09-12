using VSoftSol.Syslog.Data.Users;

namespace VSoftSol.Syslog.Web.Security;

/// <summary>
/// Whether the first-run wizard has ever been completed (PHASE_12 build item 2): true only
/// while no user account anywhere in the system has a password set. Deliberately not keyed
/// to the seeded "admin" username specifically — this install-wide definition is what a
/// fresh, never-configured database actually looks like, and it means any test (or restore
/// of an existing site's data) that has already created a real, password-bearing user is
/// correctly never treated as first-run. Registered as a singleton alongside
/// <see cref="SqliteUserStore"/> (also a singleton): once setup completes the answer can
/// never become "first run" again, so it is safe, and worth it, to stop asking the database
/// on every request from then on.
/// </summary>
public sealed class FirstRunState
{
    private readonly SqliteUserStore _users;
    private volatile bool _completed;

    public FirstRunState(SqliteUserStore users) => _users = users;

    public async Task<bool> IsFirstRunAsync(CancellationToken cancellationToken)
    {
        if (_completed)
        {
            return false;
        }

        IReadOnlyList<UserAccount> users = await _users.ListAsync(cancellationToken).ConfigureAwait(false);
        bool firstRun = !users.Any(u => u.PasswordHash is not null);
        if (!firstRun)
        {
            _completed = true;
        }

        return firstRun;
    }

    /// <summary>Called the instant the wizard finishes, so the very next request (which may
    /// arrive before this instance would otherwise have re-queried) never bounces back to
    /// <c>/setup</c>.</summary>
    public void MarkCompleted() => _completed = true;
}
