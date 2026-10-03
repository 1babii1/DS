using System.Diagnostics;
using Shared.Redis;
using StackExchange.Redis;

// Talks to Redis through Sentinel the way the services do (same connection-string rules) while the drill kills the master, and
// reports what the client saw: how long writes failed, and how many acknowledged writes the new master did not have (ADR 0036).
//   args: <seconds> <connection string>
var seconds = int.Parse(args[0]);
var connectionString = RedisConnectionString.Resilient(args[1]);
var options = ConfigurationOptions.Parse(connectionString);
await using var mux = await ConnectionMultiplexer.ConnectAsync(options);
var db = mux.GetDatabase();
await db.KeyDeleteAsync("drill:counter");

var clock = Stopwatch.StartNew();
long acknowledged = 0;
long failed = 0;
long firstFailureMs = -1;
long lastFailureMs = -1;
long longestGapMs = 0;
long lastSuccessMs = 0;
var errors = new Dictionary<string, int>();

while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
{
    try
    {
        await db.StringIncrementAsync("drill:counter");
        acknowledged++;
        var now = clock.ElapsedMilliseconds;
        longestGapMs = Math.Max(longestGapMs, now - lastSuccessMs);
        lastSuccessMs = now;
    }
    catch (Exception ex)
    {
        failed++;
        firstFailureMs = firstFailureMs < 0 ? clock.ElapsedMilliseconds : firstFailureMs;
        lastFailureMs = clock.ElapsedMilliseconds;
        var kind = ex.GetType().Name;
        errors[kind] = errors.GetValueOrDefault(kind) + 1;
    }

    await Task.Delay(20);
}

long? final = null;
for (var attempt = 0; attempt < 20 && final is null; attempt++)
{
    try
    {
        final = (long?)await db.StringGetAsync("drill:counter");
    }
    catch
    {
        await Task.Delay(500);
    }
}

Console.WriteLine($"acknowledged={acknowledged} failed={failed}");
Console.WriteLine($"first_failure_at_ms={firstFailureMs} last_failure_at_ms={lastFailureMs} longest_gap_between_successes_ms={longestGapMs}");
Console.WriteLine($"errors={string.Join(", ", errors.Select(e => $"{e.Key}:{e.Value}"))}");
Console.WriteLine($"final_counter={final} lost_acknowledged_writes={(final is null ? "unknown" : (acknowledged - final.Value).ToString())}");
Console.WriteLine($"master_now={string.Join(",", mux.GetEndPoints().Select(e => e.ToString()))}");
