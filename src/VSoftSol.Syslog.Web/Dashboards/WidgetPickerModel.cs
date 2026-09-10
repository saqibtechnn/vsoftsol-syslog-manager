using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Data.Search;
using VSoftSol.Syslog.Data.Users;
using VSoftSol.Syslog.Web.Security;

namespace VSoftSol.Syslog.Web.Dashboards;

/// <summary>
/// Feeds the guided widget picker (PHASE_09 UX gate — "pick a saved search → pick a
/// visualization → preview → add", never a JSON editor). Pure option lists plus the
/// catalogue rules from Core.
/// </summary>
public sealed class WidgetPickerModel
{
    private readonly SqliteSavedSearchStore _savedSearches;
    private readonly CurrentUserAccessor _users;
    private readonly SqliteUserStore _userStore;

    public WidgetPickerModel(SqliteSavedSearchStore savedSearches, CurrentUserAccessor users, SqliteUserStore userStore)
    {
        _savedSearches = savedSearches;
        _users = users;
        _userStore = userStore;
    }

    public IReadOnlyList<ConditionFieldInfo> GroupableFields => DashboardWidgetCatalog.GroupableFields;

    public IReadOnlyList<ConditionFieldInfo> NumericFields => DashboardWidgetCatalog.NumericFields;

    public IReadOnlyList<SystemMetricInfo> SystemMetrics => Core.Dashboards.SystemMetrics.All;

    /// <summary>The saved searches this user can pick from (their own + shared).</summary>
    public async Task<IReadOnlyList<SavedSearch>> SavedSearchesAsync(CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        UserAccount? account = await _userStore.FindByUsernameAsync(user.UserName, ct).ConfigureAwait(false);
        return account is null ? [] : await _savedSearches.ListForUserAsync(account.UserId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The visualizations that make sense for the source kind chosen so far — the picker
    /// only offers valid combinations.
    /// </summary>
    public static IReadOnlyList<VisualizationType> VisualizationsFor(WidgetSourceKind sourceKind) =>
        sourceKind == WidgetSourceKind.SystemSeries
            ? DashboardWidgetCatalog.SystemSeriesVisualizations
            : Enum.GetValues<VisualizationType>();

    /// <summary>
    /// Builds a candidate widget from the picker's selections and validates it, so the
    /// "Add" button is only enabled for a coherent choice.
    /// </summary>
    public static (WidgetDefinition Widget, ValidationResult Validation) Build(
        string title,
        WidgetSource source,
        AggregationFunction function,
        string? valueField,
        string? groupByField,
        BucketInterval bucket,
        int topN,
        VisualizationType visualization)
    {
        var widget = new WidgetDefinition
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled widget" : title.Trim(),
            Source = source,
            Aggregation = new AggregationSpec
            {
                Function = function,
                ValueField = string.IsNullOrWhiteSpace(valueField) ? null : valueField,
                GroupByField = string.IsNullOrWhiteSpace(groupByField) ? null : groupByField,
                Bucket = bucket,
                TopN = Math.Clamp(topN, 1, WidgetValidator.MaxTopN),
            },
            Visualization = visualization,
        };

        return (widget, WidgetValidator.Validate(widget));
    }
}
