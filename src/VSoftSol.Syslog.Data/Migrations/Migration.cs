using System.Security.Cryptography;
using System.Text;

namespace VSoftSol.Syslog.Data.Migrations;

/// <summary>A single forward-only schema migration loaded from an embedded SQL script.</summary>
public sealed class Migration
{
    public Migration(int version, string name, string sql)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "Migration version must be positive.");
        }

        Version = version;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Sql = sql ?? throw new ArgumentNullException(nameof(sql));
        Checksum = ComputeChecksum(sql);
    }

    public int Version { get; }

    public string Name { get; }

    public string Sql { get; }

    /// <summary>Lower-case hex SHA-256 of the script text (newlines normalised to LF).</summary>
    public string Checksum { get; }

    internal static string ComputeChecksum(string sql)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n")));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
