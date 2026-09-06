namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

internal static class AsyncEnumerableExtensions
{
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (T item in source)
        {
            list.Add(item);
        }

        return list;
    }

    public static async Task<T> FirstAsync<T>(this IAsyncEnumerable<T> source)
    {
        await foreach (T item in source)
        {
            return item;
        }

        throw new InvalidOperationException("Sequence was empty.");
    }

    public static async Task<int> CountAsync<T>(this IAsyncEnumerable<T> source)
    {
        int n = 0;
        await foreach (T _ in source)
        {
            n++;
        }

        return n;
    }
}
