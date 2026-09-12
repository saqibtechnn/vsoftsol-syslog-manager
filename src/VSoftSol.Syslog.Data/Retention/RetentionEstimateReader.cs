using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Retention;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Retention;

/// <summary>Measured inputs for <see cref="RetentionEstimator"/> — real numbers from this
/// database, not an assumed constant (UX_STANDARDS.md: never let a user configure
/// retention blind; PHASE_10 build item 1).</summary>
public sealed record RetentionInputs(double EventsPerDay, double AvgHotEventBytes, double ObservedCompressionRatio);

public sealed class RetentionEstimateReader
{
    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _time;

    public RetentionEstimateReader(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<RetentionInputs> MeasureAsync(CancellationToken cancellationToken)
    {
        string sevenDaysAgo = StorageFormat.Timestamp(_time.GetUtcNow().AddDays(-7));

        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        double eventsPerDay = 0;
        await using (SqliteCommand rate = connection.CreateCommand())
        {
            rate.CommandText = "SELECT COUNT(*) FROM events WHERE received_utc >= $since AND tier = 'hot';";
            rate.Parameters.AddWithValue("$since", sevenDaysAgo);
            object? count = await rate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            eventsPerDay = Convert.ToInt64(count, System.Globalization.CultureInfo.InvariantCulture) / 7.0;
        }

        double avgBytes = RetentionEstimator.DefaultAvgEventBytes;
        await using (SqliteCommand size = connection.CreateCommand())
        {
            size.CommandText = "SELECT AVG(LENGTH(message) + LENGTH(raw_message)) FROM events WHERE tier = 'hot' LIMIT 10000;";
            object? avg = await size.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (avg is double d)
            {
                avgBytes = d;
            }
            else if (avg is long l)
            {
                avgBytes = l;
            }
        }

        double ratio = RetentionEstimator.DefaultCompressionRatio;
        await using (SqliteCommand compressed = connection.CreateCommand())
        {
            compressed.CommandText =
                "SELECT AVG(CAST(LENGTH(raw_message) AS REAL)) FROM events WHERE tier = 'warm' LIMIT 10000;";
            object? avgWarm = await compressed.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (avgWarm is double warmBytes && avgBytes > 0)
            {
                ratio = Math.Clamp(warmBytes / avgBytes, 0.05, 1.0);
            }
        }

        return new RetentionInputs(eventsPerDay, avgBytes, ratio);
    }
}
