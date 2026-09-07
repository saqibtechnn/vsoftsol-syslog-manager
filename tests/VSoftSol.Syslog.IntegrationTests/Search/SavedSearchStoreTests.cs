using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Data.Search;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Search;

/// <summary>
/// PHASE_05 saved searches + column layouts, and the IDOR guard: "saved searches, column
/// layouts, and exports accessed by another user's ID".
/// </summary>
public sealed class SavedSearchStoreTests
{
    private static async Task<(long UserA, long UserB)> TwoUsersAsync(SqliteTestDatabase db)
    {
        long a = await Scalar(db, "INSERT INTO users (username, display_name, role_id, created_utc) VALUES ('alice','Alice',1,'t'); SELECT last_insert_rowid();");
        long b = await Scalar(db, "INSERT INTO users (username, display_name, role_id, created_utc) VALUES ('bob','Bob',2,'t'); SELECT last_insert_rowid();");
        return (a, b);
    }

    [Fact]
    public async Task Migration003_CreatesTheSearchTables()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();

        (await TableExists(db, "saved_searches")).Should().BeTrue();
        (await TableExists(db, "user_column_layouts")).Should().BeTrue();
    }

    [Fact]
    public async Task SavedSearch_CreateListGet_RoundTrips()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteSavedSearchStore(db.Factory);
        (long alice, _) = await TwoUsersAsync(db);

        long id = await store.CreateAsync(alice, "Auth failures", "failed severity:>=error", null, isShared: false, CancellationToken.None);

        SavedSearch? got = await store.GetAsync(id, alice, CancellationToken.None);
        got.Should().NotBeNull();
        got!.QueryText.Should().Be("failed severity:>=error");
        got.OwnedByCurrentUser.Should().BeTrue();

        (await store.ListForUserAsync(alice, CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task SavedSearch_ListForUser_IncludesSharedFromOthers_ButNotPrivate()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteSavedSearchStore(db.Factory);
        (long alice, long bob) = await TwoUsersAsync(db);

        await store.CreateAsync(alice, "Alice private", "a", null, isShared: false, CancellationToken.None);
        await store.CreateAsync(alice, "Alice shared", "b", null, isShared: true, CancellationToken.None);

        IReadOnlyList<SavedSearch> bobsList = await store.ListForUserAsync(bob, CancellationToken.None);

        bobsList.Select(s => s.Name).Should().ContainSingle().Which.Should().Be("Alice shared");
        bobsList.Single().OwnedByCurrentUser.Should().BeFalse();
    }

    [Fact]
    public async Task SavedSearch_Idor_OtherUserCannotReadPrivate_NorEditNorDeleteShared()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteSavedSearchStore(db.Factory);
        (long alice, long bob) = await TwoUsersAsync(db);

        long priv = await store.CreateAsync(alice, "Private", "secret", null, isShared: false, CancellationToken.None);
        long shared = await store.CreateAsync(alice, "Shared", "public", null, isShared: true, CancellationToken.None);

        // read
        (await store.GetAsync(priv, bob, CancellationToken.None)).Should().BeNull("bob cannot see alice's private search");
        (await store.GetAsync(shared, bob, CancellationToken.None)).Should().NotBeNull("shared is readable");

        // mutate
        (await store.UpdateAsync(priv, bob, "hacked", "x", null, true, CancellationToken.None)).Should().BeFalse();
        (await store.UpdateAsync(shared, bob, "hacked", "x", null, true, CancellationToken.None)).Should().BeFalse();
        (await store.DeleteAsync(priv, bob, CancellationToken.None)).Should().BeFalse();
        (await store.DeleteAsync(shared, bob, CancellationToken.None)).Should().BeFalse();

        // alice's data is intact
        (await store.GetAsync(priv, alice, CancellationToken.None))!.QueryText.Should().Be("secret");
        (await store.GetAsync(shared, alice, CancellationToken.None))!.Name.Should().Be("Shared");
    }

    [Fact]
    public async Task SavedSearch_DuplicateNameForSameOwner_Fails()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteSavedSearchStore(db.Factory);
        (long alice, _) = await TwoUsersAsync(db);

        await store.CreateAsync(alice, "Dup", "a", null, false, CancellationToken.None);
        Func<Task> again = () => store.CreateAsync(alice, "Dup", "b", null, false, CancellationToken.None);

        await again.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task ColumnLayout_SaveGetDefault_AndIdor()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteColumnLayoutStore(db.Factory);
        (long alice, long bob) = await TwoUsersAsync(db);

        await store.SaveAsync(alice, "Wide", "{\"cols\":[\"time\",\"host\"]}", isDefault: true, CancellationToken.None);
        await store.SaveAsync(alice, "Narrow", "{\"cols\":[\"time\"]}", isDefault: true, CancellationToken.None);

        ColumnLayout? def = await store.GetDefaultAsync(alice, CancellationToken.None);
        def!.Name.Should().Be("Narrow", because: "the newer default replaces the older one");
        (await store.ListForUserAsync(alice, CancellationToken.None)).Should().HaveCount(2);

        // bob sees none of alice's layouts and cannot delete them
        (await store.ListForUserAsync(bob, CancellationToken.None)).Should().BeEmpty();
        (await store.GetDefaultAsync(bob, CancellationToken.None)).Should().BeNull();
        (await store.DeleteAsync(def.Id, bob, CancellationToken.None)).Should().BeFalse();
        (await store.GetDefaultAsync(alice, CancellationToken.None)).Should().NotBeNull("bob's delete had no effect");
    }

    private static async Task<bool> TableExists(SqliteTestDatabase db, string name)
    {
        await using SqliteConnection c = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n;";
        cmd.Parameters.AddWithValue("$n", name);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<long> Scalar(SqliteTestDatabase db, string sql)
    {
        await using SqliteConnection c = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
