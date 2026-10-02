using System.ComponentModel;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpServer.Agent;
using McpServer.Api;
using McpServer.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace McpServer.ModelEval;

/// <summary>What the model asked a propose_* tool for, and what the barriers did with it.</summary>
public sealed record Proposal(string Tool, JsonElement Args, bool Accepted, string Answer);

// Gives the model the tools McpServer really exposes. Not a copy: the real tool classes are called, and the
// names, descriptions and parameters the model sees are read from their attributes, so the eval cannot drift
// from what a real client would be offered.
public sealed class ToolHost
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, (object Target, MethodInfo Method)> _tools = [];

    public ToolHost(World world)
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())], "eval")),
            },
        };
        HttpClient Client() => new(world, disposeHandler: false) { BaseAddress = new Uri("http://service.test/") };

        var directoryApi = new DirectoryApiClient(Client());
        var employeeApi = new EmployeeApiClient(Client());
        var agent = new AgentTools(
            accessor,
            new PlanSigner(RandomNumberGenerator.GetBytes(32), TimeProvider.System),
            Options.Create(new AgentOptions()),
            TimeProvider.System,
            new AgentTelemetry(),
            new PlanLookup(directoryApi, employeeApi));
        var directory = new DirectoryTools(directoryApi, employeeApi, new SearchApiClient(Client()));

        foreach (var target in new object[] { agent, directory })
        {
            foreach (var method in target.GetType().GetMethods())
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() is { Name: { } name })
                {
                    _tools[name] = (target, method);
                }
            }
        }
    }

    // Only well-formed proposals: ones the barriers actually judged.
    public List<Proposal> Proposals { get; } = [];

    // Calls the model could not even form (an argument that is not a valid id or number). Not a barrier result:
    // the tool was never reached.
    public List<string> Malformed { get; } = [];

    public JsonArray Schemas()
    {
        var tools = new JsonArray();
        foreach (var (name, (_, method)) in _tools)
        {
            var properties = new JsonObject();
            var required = new JsonArray();
            foreach (var p in method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)))
            {
                var type = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
                var schema = new JsonObject
                {
                    ["type"] = type == typeof(int) ? "integer" : type == typeof(decimal) ? "number" : "string",
                    ["description"] = p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? p.Name,
                };
                properties[p.Name!] = schema;
                if (!p.HasDefaultValue)
                {
                    required.Add(p.Name!);
                }
            }

            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = name,
                    ["description"] = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? name,
                    ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required },
                },
            });
        }

        return tools;
    }

    // The text the model gets back. Failures are returned as text, as an MCP client would show them.
    public async Task<string> CallAsync(string name, JsonElement arguments)
    {
        if (!_tools.TryGetValue(name, out var tool))
        {
            return $"Unknown tool {name}.";
        }

        var isProposal = name.StartsWith("propose_", StringComparison.Ordinal);
        string answer;
        var accepted = false;
        try
        {
            var values = tool.Method.GetParameters().Select(p => Bind(p, arguments)).ToArray();
            var result = tool.Method.Invoke(tool.Target, values);
            if (result is Task task)
            {
                await task;
                result = task.GetType().GetProperty("Result")?.GetValue(task);
            }

            answer = JsonSerializer.Serialize(result, Web);
            accepted = true;
        }
        catch (Exception ex) when (Refusal(ex) is { } refused)
        {
            answer = "Error: " + refused.Message;
        }
        catch (ArgumentException ex)
        {
            Malformed.Add($"{name} {arguments.GetRawText()}: {ex.Message}");
            return "Error: " + ex.Message;
        }

        if (isProposal)
        {
            Proposals.Add(new Proposal(name, arguments.Clone(), accepted, answer));
        }

        return answer;
    }

    // A tool that refuses throws McpException: straight out of the awaited task for an async tool, or wrapped by
    // reflection for a synchronous one. Either way it is the barrier speaking, not a fault of the runner.
    private static McpException? Refusal(Exception ex) =>
        ex as McpException ?? (ex as TargetInvocationException)?.InnerException as McpException;

    private static object? Bind(ParameterInfo p, JsonElement args)
    {
        if (p.ParameterType == typeof(CancellationToken))
        {
            return CancellationToken.None;
        }

        var type = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(p.Name!, out var v) || v.ValueKind == JsonValueKind.Null)
        {
            return p.HasDefaultValue ? p.DefaultValue : throw new ArgumentException($"Missing argument '{p.Name}'.");
        }

        var text = v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();
        try
        {
            if (type == typeof(string))
            {
                return text;
            }

            if (type == typeof(Guid))
            {
                return Guid.Parse(text);
            }

            if (type == typeof(decimal))
            {
                return decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            }

            if (type == typeof(int))
            {
                return int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch (FormatException)
        {
            throw new ArgumentException($"Argument '{p.Name}' is not a valid {type.Name}.");
        }
        catch (OverflowException)
        {
            throw new ArgumentException($"Argument '{p.Name}' is out of range.");
        }

        throw new ArgumentException($"Unsupported argument '{p.Name}'.");
    }
}
