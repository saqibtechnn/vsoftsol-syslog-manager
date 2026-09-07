namespace VSoftSol.Syslog.Data.Users;

/// <summary>A server-side session row. The auth cookie carries only <see cref="SessionId"/>.</summary>
public sealed record UserSession
{
    public required string SessionId { get; init; }

    public required long UserId { get; init; }

    public required string Username { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required DateTimeOffset LastSeenUtc { get; init; }

    public required DateTimeOffset AbsoluteExpiryUtc { get; init; }

    public string? SourceIp { get; init; }

    public string? UserAgent { get; init; }

    public DateTimeOffset? RevokedUtc { get; init; }

    public bool IsActiveAt(DateTimeOffset nowUtc, TimeSpan idleTimeout) =>
        RevokedUtc is null
        && AbsoluteExpiryUtc > nowUtc
        && LastSeenUtc + idleTimeout > nowUtc;
}
