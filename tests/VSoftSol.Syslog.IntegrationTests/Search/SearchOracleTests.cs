using System.Globalization;
using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Search;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Scoping;
using VSoftSol.Syslog.Data.Search;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace VSoftSol.Syslog.IntegrationTests.Search;

/// <summary>
/// The golden-oracle differential (TESTING_STANDARDS "Validation &amp; Evidence"): a naive
/// brute-force matcher (<see cref="QueryEvaluator"/>) and the optimised SQL compiler must
/// return identical result sets for hundreds of generated queries over the same dataset.
/// Divergence count must be zero.
/// </summary>
public sealed class SearchOracleTests(ITestOutputHelper output)
{
    private const int QueryCount = 500;
    private const int CorpusSize = 700;

    [Fact]
    public async Task SqlPath_AgreesWithTheBruteForceOracle_ForEveryGeneratedQuery()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        SearchCorpus corpus = await SearchCorpus.SeedAsync(db, CorpusSize, seed: 20260907);
        var reader = new ScopedEventReader(db.Repository, db.Factory);

        var divergences = new List<string>();
        int compared = 0;

        foreach (string queryText in GenerateQueries(QueryCount, seed: 4242))
        {
            SearchParseResult parsed = SearchQueryParser.Parse(queryText);
            if (!parsed.Success)
            {
                continue; // generator only emits valid queries; skip any that slipped through
            }

            compared++;

            var request = new SearchRequest
            {
                QueryText = queryText,
                FromUtc = corpus.WindowStart,
                ToUtc = corpus.WindowEnd.AddDays(1),
                Limit = CorpusSize * 2,
            };

            SearchResult sqlResult = await reader.SearchAsync(UserScope.Unrestricted, request, CancellationToken.None);
            sqlResult.Ok.Should().BeTrue(because: sqlResult.Error);

            HashSet<long> sqlIds = sqlResult.Rows.Select(r => r.EventId).ToHashSet();
            HashSet<long> oracleIds = corpus.Events
                .Where(e => QueryEvaluator.Matches(parsed.Query!, e.Event, e.Context))
                .Select(e => e.Event.EventId)
                .ToHashSet();

            if (!sqlIds.SetEquals(oracleIds))
            {
                var onlySql = sqlIds.Except(oracleIds).OrderBy(x => x).Take(5);
                var onlyOracle = oracleIds.Except(sqlIds).OrderBy(x => x).Take(5);
                divergences.Add(string.Create(CultureInfo.InvariantCulture,
                    $"query «{queryText}» — sql={sqlIds.Count} oracle={oracleIds.Count} " +
                    $"| only-sql: [{string.Join(",", onlySql)}] only-oracle: [{string.Join(",", onlyOracle)}]"));
            }
        }

        output.WriteLine($"compared {compared} queries against {corpus.Events.Count} events");
        foreach (string d in divergences)
        {
            output.WriteLine(d);
        }

        WriteReport(compared, divergences);

        divergences.Should().BeEmpty();
        compared.Should().BeGreaterThanOrEqualTo(450);
    }

    private static void WriteReport(int compared, IReadOnlyList<string> divergences)
    {
        string repoRoot = FindRepoRoot();
        string dir = Path.Combine(repoRoot, "docs", "evidence", "phase-05");
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        sb.AppendLine("# Phase 5 — query-compiler oracle divergence report");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Generated queries compared: **{compared}** (corpus {CorpusSize} events).");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Divergences: **{divergences.Count}**");
        sb.AppendLine();
        if (divergences.Count == 0)
        {
            sb.AppendLine("The optimised SQL path returned an identical result set to the brute-force");
            sb.AppendLine("`QueryEvaluator` for every query. Zero divergences.");
        }
        else
        {
            foreach (string d in divergences)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {d}");
            }
        }

        File.WriteAllText(Path.Combine(dir, "oracle-divergence.md"), sb.ToString());
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VSoftSol.Syslog.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private static IEnumerable<string> GenerateQueries(int count, int seed)
    {
        var rng = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            yield return BuildExpression(rng, depth: 0);
        }
    }

    private static string BuildExpression(Random rng, int depth)
    {
        if (depth < 2 && rng.Next(100) < (depth == 0 ? 55 : 25))
        {
            string left = BuildExpression(rng, depth + 1);
            string right = BuildExpression(rng, depth + 1);
            return rng.Next(3) switch
            {
                0 => $"({left}) AND ({right})",
                1 => $"({left}) OR ({right})",
                _ => $"({left}) {right}", // implicit AND
            };
        }

        string term = BuildTerm(rng);
        return rng.Next(100) < 20 ? $"NOT {term}" : term;
    }

    private static string BuildTerm(Random rng)
    {
        string W() => SearchCorpus.Words[rng.Next(SearchCorpus.Words.Length)];
        string Sev() => new[] { "emergency", "alert", "critical", "error", "warning", "notice", "info", "debug" }[rng.Next(8)];
        static string Prefix(string s) => s[..Math.Max(2, s.Length - 2)] + "*";

        string word = W();
        return rng.Next(22) switch
        {
            0 => word,
            1 => W(),
            2 => Prefix(word),
            3 => $"\"{word} {W()}\"",
            4 => $"host:{SearchCorpus.Hosts[rng.Next(SearchCorpus.Hosts.Length)]}",
            5 => $"host:{SearchCorpus.Hosts[rng.Next(SearchCorpus.Hosts.Length)][..4]}*",
            6 => $"host:!={SearchCorpus.Hosts[rng.Next(SearchCorpus.Hosts.Length)]}",
            7 => $"app:{SearchCorpus.Apps[rng.Next(SearchCorpus.Apps.Length)]}",
            8 => $"vendor:{SearchCorpus.Vendors[rng.Next(SearchCorpus.Vendors.Length)]}",
            9 => $"source_ip:{SearchCorpus.Ips[rng.Next(SearchCorpus.Ips.Length)]}",
            10 => $"source_ip:10.0.1.*",
            11 => $"severity:{Sev()}",
            12 => $"severity:>={Sev()}",
            13 => $"severity:<{Sev()}",
            14 => $"facility:{rng.Next(0, 24).ToString(CultureInfo.InvariantCulture)}",
            15 => $"parse_status:{new[] { "raw", "rfc3164", "rfc5424" }[rng.Next(3)]}",
            16 => $"protocol:{new[] { "udp", "tcp", "tls" }[rng.Next(3)]}",
            17 => $"field.user:{SearchCorpus.Users[rng.Next(SearchCorpus.Users.Length)]}",
            18 => $"field.action:{SearchCorpus.Actions[rng.Next(SearchCorpus.Actions.Length)]}",
            19 => $"field.srcport:>{rng.Next(1, 60000).ToString(CultureInfo.InvariantCulture)}",
            20 => $"device:{SearchCorpus.Hosts[rng.Next(SearchCorpus.Hosts.Length)]}",
            _ => $"stream:{new[] { "Firewall", "Auth", "Network", "Windows" }[rng.Next(4)]}",
        };
    }
}
