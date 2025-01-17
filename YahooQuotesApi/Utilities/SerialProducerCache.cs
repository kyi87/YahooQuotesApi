﻿namespace YahooQuotesApi;

internal sealed class SerialProducerCache<TKey, TResult> : IDisposable where TKey : notnull
{
    private readonly SemaphoreSlim Semaphore = new(1, 1);
    private readonly List<TKey> Buffer = new();
    private readonly Cache<TKey, TResult> Cache;
    private readonly Func<TKey[], CancellationToken, Task<Dictionary<TKey, TResult>>> Produce;

    internal SerialProducerCache(IClock clock, Duration cacheDuration, Func<TKey[], CancellationToken, Task<Dictionary<TKey, TResult>>> produce)
    {
        Cache = new Cache<TKey, TResult>(clock, cacheDuration);
        Produce = produce;
    }

    internal async Task<Dictionary<TKey, TResult>> Get(HashSet<TKey> keys, CancellationToken ct)
    {
        if (Cache.TryGetAll(keys, out var results))
            return results;

        lock (Buffer)
        {
            Buffer.AddRange(keys);
        }

        await Semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            TKey[] items;
            lock (Buffer)
            {
                items = Buffer.ToArray();
                Buffer.Clear();
            }
            if (items.Any())
            {
                results = await Produce(items, ct).ConfigureAwait(false);
                Cache.Save(results);
            }
        }
        finally
        {
            Semaphore.Release();
        }

        return Cache.Get(keys);
    }

    public void Dispose() => Semaphore.Dispose();
}
