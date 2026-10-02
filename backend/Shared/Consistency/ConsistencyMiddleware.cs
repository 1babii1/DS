using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Shared.Consistency;

public static class ConsistencyExtensions
{
    /// <summary>
    /// Reads the caller's consistency token into the request's <see cref="ReadConsistencyContext"/>, and stamps the token of
    /// the primary's current position on every successful write, so the caller can present it on its next read. Register
    /// <see cref="ReadConsistencyContext"/> as scoped.
    /// </summary>
    public static IApplicationBuilder UseConsistencyTokens(this IApplicationBuilder app, NpgsqlDataSource primary) =>
        app.Use(async (context, next) =>
        {
            var consistency = context.RequestServices.GetService<ReadConsistencyContext>();
            if (consistency is not null && Lsn.TryParse(context.Request.Headers[Lsn.Header].FirstOrDefault(), out var seen))
            {
                consistency.MinLsn = seen;
            }

            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method))
            {
                // Just before the headers go out, so the position is taken after the handler has committed.
                context.Response.OnStarting(async () =>
                {
                    if (context.Response.StatusCode < 400)
                    {
                        await using var command = primary.CreateCommand("SELECT pg_current_wal_lsn()::text");
                        if (await command.ExecuteScalarAsync(context.RequestAborted) is string text)
                        {
                            context.Response.Headers[Lsn.Header] = text;
                        }
                    }
                });
            }

            await next();
        });
}
