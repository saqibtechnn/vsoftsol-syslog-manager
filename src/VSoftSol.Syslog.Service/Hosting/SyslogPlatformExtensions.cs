using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Data;
using VSoftSol.Syslog.Data.Bundles;
using VSoftSol.Syslog.Data.Security;
using VSoftSol.Syslog.Data.Sqlite;
using VSoftSol.Syslog.Data.Users;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.Ingestion.Parsing;
using VSoftSol.Syslog.Rules.Actions;
using VSoftSol.Syslog.Rules.Alerts;
using VSoftSol.Syslog.Rules.Rules;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>
/// The single composition root. Both the Windows Service host and the Blazor Web host
/// call <see cref="AddSyslogPlatform"/>, so there is exactly one place that wires the
/// product's services (Phase 0 item 6).
/// </summary>
public static class SyslogPlatformExtensions
{
    public static IServiceCollection AddSyslogPlatform(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CollectorOptions>()
            .Bind(configuration.GetSection(CollectorOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Migrations + seed run before any other hosted service (registered first so it
        // starts first).
        services.AddHostedService<DatabaseInitializer>();

        // Data layer (Phase 1). The database path is not configurable on its own — it
        // lives under the collector data directory unless an operator overrides it in the
        // "Data" section. AddSyslogData also registers the SearchIndexMaintainer hosted
        // service, which starts after DatabaseInitializer.
        services.AddSyslogData();
        services.AddOptions<SqliteDataOptions>()
            .Bind(configuration.GetSection(SqliteDataOptions.SectionName))
            .PostConfigure<IOptions<CollectorOptions>>((data, collector) =>
            {
                if (string.IsNullOrWhiteSpace(data.DatabasePath))
                {
                    data.DatabasePath = Path.Combine(collector.Value.DataDirectory, "syslog.db");
                }
            });

        // Authentication (Phase 4). The local Argon2id provider is the v1 implementation of
        // the IAuthenticationProvider seam; an AD/LDAP provider replaces it without touching
        // the UI or the authorization layer.
        services.AddOptions<Argon2idOptions>()
            .Bind(configuration.GetSection(Argon2idOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<AuthenticationOptions>()
            .Bind(configuration.GetSection(AuthenticationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton<LocalAuthenticationProvider>();
        services.TryAddSingleton<IAuthenticationProvider>(sp => sp.GetRequiredService<LocalAuthenticationProvider>());

        // Phase 6 — the stream router. Lives here because only the composition root may
        // bridge Data (the stream store) and Rules (the router). The Web host uses it too
        // (the stream tester); the collector host uses it via the ingest enricher.
        services.TryAddSingleton<StreamRouterProvider>();

        // Phase 7 — the rules engine. The compiled rule set + the action executors, wired
        // with the secret store, the notification store, and the host-supplied action
        // limits. The Web host uses RuleSetProvider (the rule tester) and the executor
        // registry (the SMTP/webhook "test" buttons); the collector host runs the pipeline
        // and the ActionDispatchService.
        services.AddOptions<ActionExecutorOptions>()
            .Bind(configuration.GetSection(ActionExecutorOptions.SectionName))
            .PostConfigure<IOptions<IngestionOptions>>((actions, ingestion) =>
            {
                if (actions.LocalSyslogEndpoints.Count == 0)
                {
                    IngestionOptions io = ingestion.Value;
                    if (io.UdpEnabled)
                    {
                        actions.LocalSyslogEndpoints.Add($"{io.UdpBindAddress}:{io.UdpPort}");
                    }

                    if (io.TcpEnabled)
                    {
                        actions.LocalSyslogEndpoints.Add($"{io.TcpBindAddress}:{io.TcpPort}");
                    }
                }
            });
        services.AddOptions<RuleRuntimeOptions>().Bind(configuration.GetSection(RuleRuntimeOptions.SectionName));
        services.AddOptions<ActionDispatchOptions>().Bind(configuration.GetSection(ActionDispatchOptions.SectionName));

        services.TryAddSingleton<RuleSetProvider>();
        services.TryAddSingleton<RuleHitTracker>();
        services.TryAddSingleton<ActionExecutorRegistry>();
        services.TryAddSingleton<RuleRuntime>(sp => new RuleRuntime(
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptions<RuleRuntimeOptions>>().Value));

        // Delegates the executors need — composition, not seams (ADR 0008 / 0014 / 0015).
        services.TryAddSingleton<SecretResolver>(sp =>
        {
            var store = sp.GetRequiredService<VSoftSol.Syslog.Data.Secrets.SqliteSecretStore>();
            return (name, ct) => new ValueTask<string?>(store.GetAsync(name, ct));
        });
        services.TryAddSingleton<NotificationSink>(sp =>
        {
            var store = sp.GetRequiredService<VSoftSol.Syslog.Data.Notifications.SqliteNotificationStore>();
            return async (level, title, body, ruleId, ct) =>
                await store.RaiseAsync(level, title, body, ruleId, ct).ConfigureAwait(false);
        });

        // Phase 8 — the alert scheduler. The compiled alert set + the alert-action budget.
        // The Web host uses AlertSetProvider (the "would have fired" preview); the collector
        // host runs AlertEvaluationService + AlertActionDispatchService.
        services.AddOptions<AlertEvaluationOptions>().Bind(configuration.GetSection(AlertEvaluationOptions.SectionName));
        services.AddOptions<AlertRuntimeOptions>().Bind(configuration.GetSection(AlertRuntimeOptions.SectionName));
        services.TryAddSingleton<AlertSetProvider>();
        services.TryAddSingleton<AlertRuntime>(sp => new AlertRuntime(
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptions<AlertRuntimeOptions>>().Value));

        // Phase 9 — dashboards. The aggregation cache's TTL binds to the Dashboards config
        // section; the Web host reads dashboards and runs widget aggregations, the collector
        // host additionally runs the CollectorStatSampler (in AddCollectorRuntime).
        services.AddOptions<DashboardOptions>().Bind(configuration.GetSection(DashboardOptions.SectionName));
        services.TryAddSingleton<VSoftSol.Syslog.Data.Dashboards.AggregationCache>(sp => new VSoftSol.Syslog.Data.Dashboards.AggregationCache(
            sp.GetRequiredService<TimeProvider>(),
            () => sp.GetRequiredService<IOptions<DashboardOptions>>().Value.CacheTtl));

        // Phase 10 — ReportEmailSender is registered here (not AddCollectorRuntime) because
        // the Web host's "test SMTP" button (UX_STANDARDS.md §4) needs it even when run
        // standalone, without the collector runtime.
        services.TryAddSingleton<ReportEmailSender>();

        // Phase 11 — TLS/SNMP/Windows Event Log settings are bound here (not
        // AddCollectorRuntime) so the Web host's Settings pages can read them even when run
        // standalone; only the collector host actually binds a socket for them.
        services.AddOptions<TlsOptions>().Bind(configuration.GetSection(TlsOptions.SectionName)).ValidateDataAnnotations();
        services.AddOptions<SnmpOptions>().Bind(configuration.GetSection(SnmpOptions.SectionName)).ValidateDataAnnotations();
        services.AddOptions<WinEventLogOptions>().Bind(configuration.GetSection(WinEventLogOptions.SectionName)).ValidateDataAnnotations();

        // MFA / API keys / config bundles — both hosts need these stores (Web for its
        // Settings pages and the login MFA step; the collector host for the Windows Event
        // Log listener's API-key check).
        services.TryAddSingleton<SqliteMfaRecoveryCodeStore>();
        services.TryAddSingleton<SqliteApiKeyStore>();
        services.TryAddSingleton<SqliteBundleTrustStore>();
        services.TryAddSingleton<ConfigBundleExporter>(sp => new ConfigBundleExporter(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<SqliteBundleTrustStore>(),
            ResolvePatternsDirectory(sp)));
        services.TryAddSingleton<ConfigBundleImporter>(sp => new ConfigBundleImporter(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<SqliteBundleTrustStore>(),
            ResolvePatternsDirectory(sp)));

        return services;
    }

    /// <summary>Mirrors <c>PatternPackLoader</c>'s own default resolution exactly, so a
    /// bundle export/import always targets the same directory the parser actually reads.</summary>
    private static string ResolvePatternsDirectory(IServiceProvider sp)
    {
        string configured = sp.GetRequiredService<IOptions<ParsingOptions>>().Value.PatternsDirectory;
        return string.IsNullOrWhiteSpace(configured) ? Path.Combine(AppContext.BaseDirectory, "Patterns") : configured;
    }

    /// <summary>
    /// Registers the collector runtime — the UDP/TCP listeners, the bounded channel, the
    /// disk spill queue, and the ingest pipeline (Phase 2). The Web host does not call this,
    /// so the UI process never binds a listener.
    /// </summary>
    public static IServiceCollection AddCollectorRuntime(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSyslogIngestion();
        services.AddOptions<IngestionOptions>()
            .Bind(configuration.GetSection(IngestionOptions.SectionName))
            .PostConfigure<IOptions<CollectorOptions>>((ingestion, collector) =>
            {
                if (string.IsNullOrWhiteSpace(ingestion.SpillDirectory))
                {
                    ingestion.SpillDirectory = Path.Combine(collector.Value.DataDirectory, "spill");
                }
            });

        // The ingest enricher (PHASE_06 items 3 & 6, PHASE_07 item 2): resolve the source IP
        // to a device (auto-discovering unknown sources), route the event to its streams,
        // then evaluate the rule set — all once, before the batch commits. Side-effecting
        // actions are attached to the event and persisted to the outbox in the same
        // transaction; the ActionDispatchService executes them OFF this thread.
        services.TryAddSingleton<EventEnricher>(sp =>
        {
            var resolver = sp.GetRequiredService<VSoftSol.Syslog.Data.Devices.DeviceResolver>();
            var routers = sp.GetRequiredService<StreamRouterProvider>();
            var rules = sp.GetRequiredService<RuleSetProvider>();
            var runtime = sp.GetRequiredService<RuleRuntime>();
            var hits = sp.GetRequiredService<RuleHitTracker>();
            var time = sp.GetRequiredService<TimeProvider>();

            return async (parsed, cancellationToken) =>
            {
                long? deviceId = await resolver.ResolveAsync(parsed.SourceIp, parsed.Hostname, cancellationToken)
                    .ConfigureAwait(false);
                VSoftSol.Syslog.Rules.Streams.StreamRouter router =
                    await routers.GetAsync(cancellationToken).ConfigureAwait(false);
                VSoftSol.Syslog.Core.Events.SyslogEvent routed = parsed.WithRouting(deviceId, router.Route(parsed));

                RuleSet ruleSet = await rules.GetAsync(cancellationToken).ConfigureAwait(false);
                if (ruleSet.RuleCount == 0)
                {
                    return routed;
                }

                IReadOnlyList<long> groups = deviceId is { } id
                    ? await resolver.ResolveGroupsAsync(id, cancellationToken).ConfigureAwait(false)
                    : [];

                DateTimeOffset now = time.GetUtcNow();
                IReadOnlyList<CompiledRule> matched = ruleSet.Match(routed, groups, now);
                if (matched.Count == 0)
                {
                    return routed;
                }

                foreach (CompiledRule rule in matched)
                {
                    hits.Record(rule.RuleId, now);
                }

                RuleOutcome outcome = runtime.Apply(matched, routed);
                if (!outcome.HasWork)
                {
                    return routed;
                }

                var pending = new List<VSoftSol.Syslog.Core.Rules.PendingRuleAction>(outcome.Dispatches.Count + 1);
                foreach (PendingDispatch d in outcome.Dispatches)
                {
                    pending.Add(new VSoftSol.Syslog.Core.Rules.PendingRuleAction(
                        d.RuleId, d.RuleName, d.ActionIndex,
                        VSoftSol.Syslog.Core.Rules.RuleActionInfo.Kind(d.Action),
                        VSoftSol.Syslog.Data.Rules.RuleJson.SerializeAction(d.Action),
                        d.WasEscalation));
                }

                if (outcome.Storm is { } storm)
                {
                    var summary = new VSoftSol.Syslog.Core.Rules.RaiseNotificationAction
                    {
                        Level = VSoftSol.Syslog.Core.Rules.NotificationLevel.Warning,
                        Title = "Alert-storm protection engaged",
                        Body = $"{storm.TotalCollapsed} outbound action(s) suppressed in the last minute: " +
                               string.Join(", ", storm.ByKind.Select(kv => $"{kv.Value}× {kv.Key}")),
                    };
                    pending.Add(new VSoftSol.Syslog.Core.Rules.PendingRuleAction(
                        0, "system", 0, "notify",
                        VSoftSol.Syslog.Data.Rules.RuleJson.SerializeAction(summary), WasEscalation: false));
                }

                return routed.WithRuleOutcome(outcome.Tags, outcome.ExtraStreamIds, pending);
            };
        });

        // The dispatcher executes queued actions off the ingest thread (PHASE_07 isolation).
        services.AddHostedService<ActionDispatchService>();

        // Phase 8 — the scheduled alert evaluator and its own action dispatcher. Collector
        // host only (the Web host never schedules evaluations).
        services.AddHostedService<AlertEvaluationService>();
        services.AddHostedService<AlertActionDispatchService>();

        // Phase 9 — the collector-health sampler. Writes collector_stat_samples for the
        // Collector Health dashboard. Collector host only; the Web host, run standalone,
        // has no live counters to sample.
        services.AddOptions<CollectorStatOptions>().Bind(configuration.GetSection(CollectorStatOptions.SectionName));
        services.AddHostedService<CollectorStatSampler>();

        // Phase 10 — retention tiering (Hot/Warm/Cold/Delete + archive verification) and the
        // scheduled report engine. Both collector-host only, same disposition as the alert
        // scheduler: the Web host reads/writes retention policy and reports, but only the
        // collector host runs the background schedulers.
        services.AddOptions<RetentionOptions>().Bind(configuration.GetSection(RetentionOptions.SectionName));
        services.AddHostedService<RetentionTieringService>();

        services.AddOptions<ReportSchedulerOptions>().Bind(configuration.GetSection(ReportSchedulerOptions.SectionName));
        services.AddHostedService<ReportSchedulerService>();

        // Phase 11 — the TLS/SNMP/Windows Event Log listeners need a certificate/community/
        // API-key resolver that can reach the Data-layer secret and key stores; only the
        // composition root can build that delegate (ADR 0008/0014/0015 precedent). The
        // listener types themselves are registered by AddSyslogIngestion (Ingestion.AddSyslogIngestion),
        // called from AddCollectorRuntime below.
        services.TryAddSingleton<TlsCertificateProvider>(sp =>
        {
            IOptions<TlsOptions> options = sp.GetRequiredService<IOptions<TlsOptions>>();
            SecretResolver secrets = sp.GetRequiredService<SecretResolver>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("TlsCertificateProvider");
            TlsCertificateProvider provider = async ct =>
            {
                TlsOptions o = options.Value;
                try
                {
                    if (!string.IsNullOrWhiteSpace(o.CertificateThumbprint))
                    {
                        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
                        store.Open(OpenFlags.ReadOnly);
                        X509Certificate2Collection found =
                            store.Certificates.Find(X509FindType.FindByThumbprint, o.CertificateThumbprint, validOnly: false);
                        return found.Count > 0 ? found[0] : null;
                    }

                    if (!string.IsNullOrWhiteSpace(o.PfxPath) && File.Exists(o.PfxPath))
                    {
                        string? password = string.IsNullOrWhiteSpace(o.PfxPasswordSecretName)
                            ? null
                            : await secrets(o.PfxPasswordSecretName, ct).ConfigureAwait(false);
#pragma warning disable SYSLIB0057 // net8.0 target; X509CertificateLoader is the .NET 9+ replacement, not yet available here.
                        return new X509Certificate2(o.PfxPath, password ?? string.Empty,
                            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
#pragma warning restore SYSLIB0057
                    }
                }
                catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
                {
                    logger.LogError(ex, "Failed to load the TLS listener's server certificate.");
                }

                return null;
            };

            return provider;
        });

        services.TryAddSingleton<SnmpCommunityProvider>(sp =>
        {
            IOptions<SnmpOptions> options = sp.GetRequiredService<IOptions<SnmpOptions>>();
            SecretResolver secrets = sp.GetRequiredService<SecretResolver>();
            SnmpCommunityProvider provider = async ct =>
            {
                string? name = options.Value.CommunitySecretName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    return [];
                }

                string? value = await secrets(name, ct).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(value)
                    ? []
                    : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            };

            return provider;
        });

        services.TryAddSingleton<WinEventLogApiKeyValidator>(sp =>
        {
            var apiKeys = sp.GetRequiredService<SqliteApiKeyStore>();
            return (key, sourceIp, ct) => new ValueTask<bool>(apiKeys.IsValidAsync("wineventlog", key, sourceIp, ct));
        });

        services.AddSingleton<ISyslogListener, TlsSyslogListener>();
        services.AddSingleton<ISyslogListener, SnmpTrapListener>();
        services.AddSingleton<ISyslogListener, WinEventLogListener>();

        // Phase 11 — collector self-monitoring (items 5-7). Collector host only.
        services.AddOptions<SelfMonitoringOptions>().Bind(configuration.GetSection(SelfMonitoringOptions.SectionName));
        services.AddHostedService<SelfMonitoringService>();

        return services;
    }

    internal static string ResolveDataDirectory(this IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<CollectorOptions>>().Value.DataDirectory;
}
