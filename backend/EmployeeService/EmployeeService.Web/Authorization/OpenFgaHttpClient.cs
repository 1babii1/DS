using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmployeeService.Application.Authorization;
using Microsoft.Extensions.Options;

namespace EmployeeService.Web.Authorization;

public sealed class FgaException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class OpenFgaStoreState
{
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public string? StoreId { get; set; }
}

/// <summary>
/// OpenFGA over its HTTP API. The store and the model are found or created on first use (the model from <c>fga-model.json</c>, embedded; <c>scripts/check-fga-model.sh</c> keeps it equal to
/// <c>deploy/fga/model.fga</c>, the file the model's own tests are written against), so there is nothing to run before the service: if OpenFGA is not there yet, the first call fails and
/// the next one tries again. A check always asks for the higher consistency level, which bypasses the server's query cache. With the cache on (it is off by
/// default, and off in the tests, so the difference is not shown here) the default level could keep saying no for a while after an administrator has made
/// someone a manager, and for authorization that is the wrong side to be stale on.
/// </summary>
public sealed class OpenFgaHttpClient(HttpClient http, IOptions<DepartmentAuthorizationOptions> options, OpenFgaStoreState state) : IFgaClient
{
    public async Task<bool> CheckAsync(FgaTuple tuple, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["tuple_key"] = Key(tuple),
            ["consistency"] = "HIGHER_CONSISTENCY",
        };
        var reply = await PostAsync($"/stores/{await StoreAsync(cancellationToken)}/check", body, cancellationToken);
        return reply?["allowed"]?.GetValue<bool>() ?? throw new FgaException("OpenFGA's answer to a check had no \"allowed\"");
    }

    public async Task WriteAsync(IReadOnlyCollection<FgaTuple> writes, IReadOnlyCollection<FgaTuple> deletes, CancellationToken cancellationToken)
    {
        if (writes.Count == 0 && deletes.Count == 0)
        {
            return;
        }

        var body = new JsonObject();
        if (writes.Count > 0)
        {
            body["writes"] = new JsonObject { ["tuple_keys"] = Keys(writes), ["on_duplicate"] = "ignore" };
        }

        if (deletes.Count > 0)
        {
            body["deletes"] = new JsonObject { ["tuple_keys"] = Keys(deletes), ["on_missing"] = "ignore" };
        }

        await PostAsync($"/stores/{await StoreAsync(cancellationToken)}/write", body, cancellationToken);
    }

    public async Task<IReadOnlyList<FgaTuple>> ReadAsync(string obj, string? relation, CancellationToken cancellationToken)
    {
        var found = new List<FgaTuple>();
        string? continuation = null;
        do
        {
            var key = new JsonObject { ["object"] = obj };
            if (relation is not null)
            {
                key["relation"] = relation;
            }

            var body = new JsonObject { ["tuple_key"] = key, ["page_size"] = 100 };
            if (!string.IsNullOrEmpty(continuation))
            {
                body["continuation_token"] = continuation;
            }

            var reply = await PostAsync($"/stores/{await StoreAsync(cancellationToken)}/read", body, cancellationToken);
            foreach (var tuple in reply?["tuples"]?.AsArray() ?? [])
            {
                var k = tuple?["key"] ?? throw new FgaException("OpenFGA returned a tuple without a key");
                found.Add(new FgaTuple(k["user"]!.GetValue<string>(), k["relation"]!.GetValue<string>(), k["object"]!.GetValue<string>()));
            }

            continuation = reply?["continuation_token"]?.GetValue<string>();
        }
        while (!string.IsNullOrEmpty(continuation));

        return found;
    }

    private static JsonObject Key(FgaTuple t) => new() { ["user"] = t.User, ["relation"] = t.Relation, ["object"] = t.Object };

    private static JsonArray Keys(IEnumerable<FgaTuple> tuples) => new(tuples.Select(t => (JsonNode)Key(t)).ToArray());

    private async Task<string> StoreAsync(CancellationToken cancellationToken)
    {
        if (state.StoreId is { } known)
        {
            return known;
        }

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (state.StoreId is { } already)
            {
                return already;
            }

            var name = options.Value.StoreName;
            string? id = null;
            string? token = null;
            do
            {
                var page = await GetAsync($"/stores?page_size=100{(token is null ? "" : "&continuation_token=" + Uri.EscapeDataString(token))}", cancellationToken);
                id = page?["stores"]?.AsArray().FirstOrDefault(s => s?["name"]?.GetValue<string>() == name)?["id"]?.GetValue<string>();
                token = page?["continuation_token"]?.GetValue<string>();
            }
            while (id is null && !string.IsNullOrEmpty(token));

            id ??= (await PostAsync("/stores", new JsonObject { ["name"] = name }, cancellationToken))?["id"]?.GetValue<string>()
                ?? throw new FgaException("OpenFGA created a store and did not say its id");

            await EnsureModelAsync(id, cancellationToken);
            state.StoreId = id;
            return id;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    // Writes the model if the store has none or its latest differs from the one in this build. A model is immutable in OpenFGA; a changed one is a new version.
    private async Task EnsureModelAsync(string id, CancellationToken cancellationToken)
    {
        var wanted = LoadModel();
        var latest = (await GetAsync($"/stores/{id}/authorization-models?page_size=1", cancellationToken))?["authorization_models"]?.AsArray().FirstOrDefault();
        if (latest is not null && Canonical(latest["type_definitions"]) == Canonical(wanted["type_definitions"]))
        {
            return;
        }

        await PostAsync($"/stores/{id}/authorization-models", wanted, cancellationToken);
    }

    private static JsonObject LoadModel()
    {
        using var stream = typeof(OpenFgaHttpClient).Assembly.GetManifestResourceStream("fga-model.json")
            ?? throw new FgaException("The authorization model is not embedded in this build");
        return JsonNode.Parse(stream)!.AsObject();
    }

    // The same content in the same order, so that two spellings of one model compare equal.
    private static string Canonical(JsonNode? node) => node switch
    {
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"\"{p.Key}\":{Canonical(p.Value)}")) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        null => "null",
        _ => node.ToJsonString(),
    };

    private async Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(path, cancellationToken);
            return await ReadAsync(response, path, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new FgaException($"OpenFGA could not be reached ({path})", ex);
        }
    }

    private async Task<JsonNode?> PostAsync(string path, JsonNode body, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(path, body, cancellationToken);
            return await ReadAsync(response, path, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new FgaException($"OpenFGA could not be reached ({path})", ex);
        }
    }

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new FgaException($"OpenFGA answered {(int)response.StatusCode} to {path}: {text}");
        }

        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }
}
