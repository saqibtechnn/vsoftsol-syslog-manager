namespace VSoftSol.Syslog.Data.Migrations;

/// <summary>A migration could not be applied, or an applied migration has drifted.</summary>
public sealed class MigrationException : Exception
{
    public MigrationException(string message)
        : base(message)
    {
    }

    public MigrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
