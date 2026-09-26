using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace DirectoryService.Application.Cache;

/// <summary>
/// Обновление кэша после коммита. Кэш здесь - ускоритель, а не источник истины:
/// данные уже записаны в базу, и падение Redis не должно превращать успешную
/// команду в ошибку. Иначе клиент получает 500 на уже созданной сущности и при
/// повторе упирается в конфликт по уникальному идентификатору.
/// Промах или устаревшая запись в L2 живут до истечения TTL - это дешевле, чем
/// отказ операции, которую уже нельзя откатить.
/// </summary>
public static class BestEffortCache
{
    public static async Task SetOrIgnoreAsync<T>(
        this HybridCache cache,
        ILogger logger,
        string key,
        T value,
        HybridCacheEntryOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            await cache.SetAsync(key, value, options, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to cache {CacheKey}; the write itself succeeded", key);
        }
    }

    public static async Task RemoveOrIgnoreAsync(
        this HybridCache cache,
        ILogger logger,
        string key,
        CancellationToken cancellationToken)
    {
        try
        {
            await cache.RemoveAsync(key, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to evict {CacheKey}; it will expire on its own", key);
        }
    }

    public static async Task RemoveOrIgnoreAsync(
        this HybridCache cache,
        ILogger logger,
        IEnumerable<string> keys,
        CancellationToken cancellationToken)
    {
        var keyList = keys as IReadOnlyCollection<string> ?? keys.ToList();

        try
        {
            await cache.RemoveAsync(keyList, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "Failed to evict {CacheKeys}; they will expire on their own", string.Join(", ", keyList));
        }
    }
}