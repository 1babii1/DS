using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using McpServer.Agent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using Xunit.Abstractions;
using static McpServer.IntegrationTests.AgentTestSupport;

namespace McpServer.IntegrationTests;

// Scores the barriers between what a model emits and any change to data, against a reviewable corpus
// (Evals/guardrails.json). This is deliberately NOT a model eval: it says nothing about whether a model
// picks the right tool, only that whatever it picks, a manipulated or mistaken output cannot get past
// the checks. Every case is run and the failures are listed together, so one run shows the whole picture.
public class GuardrailEvalTests(ITestOutputHelper output)
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    private sealed class Clock : TimeProvider;

    private static JsonElement Corpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Evals", "guardrails.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    [Fact]
    public void The_corpus_is_present_and_has_both_kinds_of_case()
    {
        Assert.True(Corpus().GetProperty("proposals").GetArrayLength() >= 30);
        Assert.True(Corpus().GetProperty("signedPlans").GetArrayLength() >= 10);
    }

    [Fact]
    public async Task Every_proposal_is_refused_or_accepted_as_the_corpus_expects()
    {
        var signer = new PlanSigner(Key, new Clock());
        var tools = new AgentTools(
            new HttpContextAccessor
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", User.ToString())], "test")),
                },
            },
            signer,
            Options.Create(new AgentOptions()),
            new Clock(),
            new AgentTelemetry(),
            FakeOrg.LenientOrg().Lookup());

        var failures = new List<string>();
        var cases = Corpus().GetProperty("proposals").EnumerateArray().ToList();
        foreach (var c in cases)
        {
            var id = c.GetProperty("id").GetString()!;
            var why = c.GetProperty("why").GetString()!;
            var expect = c.GetProperty("expect").GetString();

            PlanProposal? proposal = null;
            McpException? refused = null;
            try
            {
                proposal = await Call(tools, c.GetProperty("tool").GetString()!, c.GetProperty("args"));
            }
            catch (McpException ex)
            {
                refused = ex;
            }

            if (expect == "refused")
            {
                if (refused is null)
                {
                    failures.Add($"{id} ({why}): was ACCEPTED, expected refused");
                }

                continue;
            }

            if (refused is not null)
            {
                failures.Add($"{id} ({why}): was REFUSED ({refused.Message}), expected proposed");
                continue;
            }

            var plan = signer.Verify(proposal!.PlanToken, User);
            if (c.TryGetProperty("steps", out var steps) && plan.Steps.Count != steps.GetInt32())
            {
                failures.Add($"{id} ({why}): {plan.Steps.Count} steps, expected {steps.GetInt32()}");
            }

            if (c.TryGetProperty("maxAmount", out var max))
            {
                var top = plan.Steps.Where(s => s.Amount is not null).Select(s => s.Amount!.Value).DefaultIfEmpty(0m).Max();
                if (top != max.GetDecimal())
                {
                    failures.Add($"{id} ({why}): largest amount {top}, expected {max.GetDecimal()}");
                }
            }

            if (c.TryGetProperty("summaryContains", out var contains))
            {
                foreach (var needle in contains.EnumerateArray().Select(n => n.GetString()!))
                {
                    if (!proposal.Steps.Any(s => s.Contains(needle, StringComparison.Ordinal)))
                    {
                        failures.Add($"{id} ({why}): summary lacks {needle}");
                    }
                }
            }

            if (proposal.Steps.Any(s => s.Contains('\n') || s.Contains('\r')))
            {
                failures.Add($"{id} ({why}): a summary spans more than one line");
            }
        }

        Report("proposals", cases.Count, failures);
    }

    [Fact]
    public async Task Every_signed_but_hostile_plan_ends_as_the_corpus_expects()
    {
        var signer = new PlanSigner(Key, new Clock());
        var failures = new List<string>();
        var cases = Corpus().GetProperty("signedPlans").EnumerateArray().ToList();

        foreach (var c in cases)
        {
            var id = c.GetProperty("id").GetString()!;
            var why = c.GetProperty("why").GetString()!;
            var stepSpecs = c.GetProperty("steps").EnumerateArray().Select(s => s.GetString()!).ToList();

            var employees = new RecordingService((_, _) => stepSpecs.Contains("hire:refused")
                ? RecordingService.Json(HttpStatusCode.Forbidden, "{}")
                : RecordingService.Ok(Guid.NewGuid()));
            var rewards = new RecordingService((_, _) => RecordingService.Ok(Guid.NewGuid()));

            // Through the real signer, as a plan that reached the executor would have come.
            var plan = PlanOf([.. stepSpecs.Select(Step)]);
            plan = signer.Verify(signer.Sign(plan with { UserId = User }), User);

            var report = await Executor(employees, rewards).ExecuteAsync(plan, CancellationToken.None);

            var got = report.Steps.Select(s => s.Outcome.ToString()).ToArray();
            var want = c.GetProperty("expectOutcomes").EnumerateArray().Select(o => o.GetString()!).ToArray();
            if (!got.SequenceEqual(want))
            {
                failures.Add($"{id} ({why}): outcomes [{string.Join(",", got)}], expected [{string.Join(",", want)}]");
            }

            var wantRewards = c.GetProperty("rewardsRequests").GetInt32();
            if (rewards.Requests.Count != wantRewards)
            {
                failures.Add($"{id} ({why}): {rewards.Requests.Count} grant requests sent, expected {wantRewards}");
            }
        }

        Report("signed plans", cases.Count, failures);
    }

    private void Report(string name, int total, List<string> failures)
    {
        output.WriteLine($"guardrail eval - {name}: {total - failures.Count}/{total} as expected");
        foreach (var f in failures)
        {
            output.WriteLine("  FAIL " + f);
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {total} {name} cases failed:\n" + string.Join("\n", failures));
    }

    // ---- corpus -> calls ---------------------------------------------------------------------------

    private static Task<PlanProposal> Call(AgentTools tools, string tool, JsonElement a) => tool switch
    {
        "propose_hire_employee" => tools.ProposeHire(
            Text(a, "fullName", "Anna Ivanova")!,
            Text(a, "email", "anna@x.test")!,
            Id(a, "departmentId", Dept),
            Id(a, "positionId", Pos)),
        "propose_transfer_employee" => tools.ProposeTransfer(
            Id(a, "employeeId", Guid.NewGuid()), Id(a, "departmentId", Dept), Id(a, "positionId", Pos)),
        "propose_grant_currency" => tools.ProposeGrant(
            Id(a, "employeeId", Guid.NewGuid()),
            a.GetProperty("amount").GetDecimal(),
            Text(a, "reason", "thanks")!),
        _ => throw new InvalidOperationException($"Unknown tool {tool}"),
    };

    private static string? Text(JsonElement a, string name, string? fallback)
    {
        if (!a.TryGetProperty(name, out var v))
        {
            return fallback;
        }

        var s = v.GetString();
        return s switch
        {
            "LONG201" => new string('a', 201),
            "LONG501" => new string('a', 501),

            // JSON cannot carry an unpaired surrogate, so the corpus names it and it is built here.
            "LONESURROGATE" => "broken \uD83C",
            _ => s,
        };
    }

    private static Guid Id(JsonElement a, string name, Guid fallback) =>
        a.TryGetProperty(name, out var v) ? Guid.Parse(v.GetString()!) : fallback;

    // "hire", "hire:refused", "hire:badname", "kind99", "grant:<amount>:from<N>[:direct]", "grant:<amount>:nobody"
    private static PlanStep Step(string spec)
    {
        var parts = spec.Split(':');
        switch (parts[0])
        {
            case "hire":
                return parts.Length > 1 && parts[1] == "badname" ? Hire() with { FullName = "Anna\nX" } : Hire();

            case "kind99":
                return new PlanStep((StepKind)99, "unknown");

            case "grant":
            {
                var amount = decimal.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                if (parts[2] == "nobody")
                {
                    return new PlanStep(StepKind.GrantCurrency, "g", Amount: amount, Reason: "r");
                }

                var from = int.Parse(parts[2].Replace("from", string.Empty));
                var step = new PlanStep(StepKind.GrantCurrency, "g", EmployeeFromStep: from, Amount: amount, Reason: "r");
                return parts.Length > 3 && parts[3] == "direct" ? step with { EmployeeId = Guid.NewGuid() } : step;
            }

            default:
                throw new InvalidOperationException($"Unknown step spec {spec}");
        }
    }
}
