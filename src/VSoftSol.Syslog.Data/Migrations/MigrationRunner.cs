using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Migrations;

/// <summary>
/// Applies forward-only, versioned, checksummed SQL migrations. Idempotent: re-running
/// after every migration is applied is a no-op that still verifies checksums, so schema
/// drift (an applied script edited in place) fails loudly.
/// </summary>
public sealed partial class MigrationRunner
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<MigrationRunner> _logger;
    private readonly IReadOnlyList<Migration> _migrations;

    public MigrationRunner(SqliteConnectionFactory factory, ILogger<MigrationRunner> logger)
        : this(factory, logger, LoadEmbeddedMigrations())
    {
    }

    internal MigrationRunner(SqliteConnectionFactory factory, ILogger<MigrationRunner> logger, IReadOnlyList<Migration> migrations)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _migrations = migrations ?? throw new ArgumentNullException(nameof(migrations));
    }

    public IReadOnlyList<Migration> Migrations => _migrations;

    /// <summary>Applies every pending migration in order. Returns the number applied.</summary>
    public async Task<int> MigrateAsync(CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        await EnsureSchemaVersionTableAsync(connection, cancellationToken).ConfigureAwait(false);
        Dictionary<int, string> applied = await ReadAppliedAsync(connection, cancellationToken).ConfigureAwait(false);

        int count = 0;
        foreach (Migration migration in _migrations.OrderBy(m => m.Version))
        {
            if (applied.TryGetValue(migration.Version, out string? recordedChecksum))
            {
                if (!string.Equals(recordedChecksum, migration.Checksum, StringComparison.Ordinal))
                {
                    throw new MigrationException(
                        $"Migration {migration.Version:D3} ({migration.Name}) has changed since it was applied " +
                        $"(recorded {recordedChecksum}, current {migration.Checksum}). Migrations are immutable once applied.");
                }

                continue;
            }

            await ApplyAsync(connection, migration, cancellationToken).ConfigureAwait(false);
            count++;
        }

        if (count > 0)
        {
            _logger.LogInformation("Applied {Count} database migration(s); schema now at version {Version}.",
                count, _migrations.Max(m => m.Version));
        }

        return count;
    }

    private static async Task ApplyAsync(SqliteConnection connection, Migration migration, CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (SqliteCommand script = connection.CreateCommand())
            {
                script.Transaction = transaction;
                script.CommandText = migration.Sql;
                await script.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (SqliteCommand record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = """
                    INSERT INTO schema_version (version, name, checksum, applied_utc)
                    VALUES ($version, $name, $checksum, $applied);
                    """;
                record.Parameters.AddWithValue("$version", migration.Version);
                record.Parameters.AddWithValue("$name", migration.Name);
                record.Parameters.AddWithValue("$checksum", migration.Checksum);
                record.Parameters.AddWithValue("$applied", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw new MigrationException(
                $"Migration {migration.Version:D3} ({migration.Name}) failed and was rolled back: {ex.Message}", ex);
        }
    }

    private static async Task EnsureSchemaVersionTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_version (
                version      INTEGER PRIMARY KEY,
                name         TEXT NOT NULL,
                checksum     TEXT NOT NULL,
                applied_utc  TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Dictionary<int, string>> ReadAppliedAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, string>();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT version, checksum FROM schema_version;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[reader.GetInt32(0)] = reader.GetString(1);
        }

        return result;
    }

    internal static IReadOnlyList<Migration> LoadEmbeddedMigrations()
    {
        Assembly assembly = typeof(MigrationRunner).Assembly;
        const string marker = ".Migrations.Scripts.";
        var migrations = new List<Migration>();

        foreach (string resourceName in assembly.GetManifestResourceNames())
        {
            int markerIndex = resourceName.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0 || !resourceName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string fileName = resourceName[(markerIndex + marker.Length)..];
            Match match = MigrationFileName().Match(fileName);
            if (!match.Success)
            {
                throw new MigrationException($"Embedded migration '{fileName}' is not named NNN_description.sql.");
            }

            using Stream stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new MigrationException($"Could not open embedded migration '{resourceName}'.");
            using var streamReader = new StreamReader(stream);
            string sql = streamReader.ReadToEnd();

            migrations.Add(new Migration(
                int.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture),
                match.Groups["name"].Value,
                sql));
        }

        if (migrations.Count == 0)
        {
            throw new MigrationException("No embedded migration scripts were found.");
        }

        List<Migration> ordered = migrations.OrderBy(m => m.Version).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Version != i + 1)
            {
                throw new MigrationException(
                    $"Migration versions must be a gap-free sequence starting at 1; found {ordered[i].Version} at position {i + 1}.");
            }
        }

        return ordered;
    }

    [GeneratedRegex(@"^(?<version>\d{3})_(?<name>[a-z0-9_]+)\.sql$", RegexOptions.IgnoreCase)]
    private static partial Regex MigrationFileName();
}
