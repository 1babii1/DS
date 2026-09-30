using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using McpServer.Agent;

namespace McpServer.IntegrationTests;

public class PlanSignerTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid User = Guid.NewGuid();

    private static Plan SamplePlan(Guid? user = null, DateTimeOffset? expires = null) => new(
        Guid.NewGuid(),
        user ?? User,
        T0,
        expires ?? T0.AddMinutes(10),
        [new PlanStep(StepKind.GrantCurrency, "Grant 500", EmployeeId: Guid.NewGuid(), Amount: 500m, Reason: "spot bonus")]);

    private static (PlanSigner Signer, FixedClock Clock) Make(byte[]? key = null)
    {
        var clock = new FixedClock(T0);
        return (new PlanSigner(key ?? Key, clock), clock);
    }

    [Fact]
    public void A_signed_plan_verifies_for_its_user_and_comes_back_unchanged()
    {
        var (signer, _) = Make();
        var plan = SamplePlan();

        var back = signer.Verify(signer.Sign(plan), User);

        Assert.Equal(plan.Id, back.Id);
        Assert.Equal(plan.Steps[0].Amount, back.Steps[0].Amount);
        Assert.Equal(plan.Steps[0].EmployeeId, back.Steps[0].EmployeeId);
    }

    [Fact]
    public void Changing_the_amount_after_signing_is_rejected_even_with_the_original_signature()
    {
        var (signer, _) = Make();
        var token = signer.Sign(SamplePlan());
        var parts = token.Split('.');

        // What an attacker holding the token would do: edit a step and keep the old signature.
        var json = Encoding.UTF8.GetString(Decode(parts[1])).Replace("500", "5000");
        var forged = $"{parts[0]}.{Encode(Encoding.UTF8.GetBytes(json))}.{parts[2]}";

        var ex = Assert.Throws<PlanTokenException>(() => signer.Verify(forged, User));
        Assert.Equal(PlanTokenProblem.BadSignature, ex.Problem);
    }

    [Fact]
    public void A_token_signed_with_another_key_is_rejected()
    {
        var (mine, _) = Make();
        var (theirs, _) = Make(RandomNumberGenerator.GetBytes(32));

        var ex = Assert.Throws<PlanTokenException>(() => mine.Verify(theirs.Sign(SamplePlan()), User));
        Assert.Equal(PlanTokenProblem.BadSignature, ex.Problem);
    }

    [Fact]
    public void A_flipped_signature_is_rejected()
    {
        var (signer, _) = Make();
        var parts = signer.Sign(SamplePlan()).Split('.');
        var sig = Decode(parts[2]);
        sig[0] ^= 0xFF;

        var ex = Assert.Throws<PlanTokenException>(() => signer.Verify($"{parts[0]}.{parts[1]}.{Encode(sig)}", User));
        Assert.Equal(PlanTokenProblem.BadSignature, ex.Problem);
    }

    [Fact]
    public void A_plan_is_valid_until_the_instant_it_expires_and_not_after()
    {
        var (signer, clock) = Make();
        var token = signer.Sign(SamplePlan());

        clock.Now = T0.AddMinutes(10).AddTicks(-1);
        signer.Verify(token, User);

        clock.Now = T0.AddMinutes(10);
        var ex = Assert.Throws<PlanTokenException>(() => signer.Verify(token, User));
        Assert.Equal(PlanTokenProblem.Expired, ex.Problem);
    }

    [Fact]
    public void Another_users_plan_cannot_be_confirmed()
    {
        var (signer, _) = Make();
        var token = signer.Sign(SamplePlan());

        var ex = Assert.Throws<PlanTokenException>(() => signer.Verify(token, Guid.NewGuid()));
        Assert.Equal(PlanTokenProblem.WrongUser, ex.Problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("v1.only-two")]
    [InlineData("v2.aaaa.bbbb")]
    [InlineData("v1.!!!.???")]
    public void Malformed_tokens_are_rejected_as_malformed(string token)
    {
        var (signer, _) = Make();

        var ex = Assert.Throws<PlanTokenException>(() => signer.Verify(token, User));
        Assert.Equal(PlanTokenProblem.Malformed, ex.Problem);
    }

    [Fact]
    public void A_validly_signed_payload_that_is_not_a_plan_is_malformed_not_a_crash()
    {
        var (signer, _) = Make();
        var payload = Encode(Encoding.UTF8.GetBytes("""{"steps":[]}"""));
        var sig = Encode(HMACSHA256.HashData(Key, Encoding.ASCII.GetBytes(payload)));

        var ex = Assert.Throws<PlanTokenException>(() => signer.Verify($"v1.{payload}.{sig}", User));
        Assert.Equal(PlanTokenProblem.Malformed, ex.Problem);
    }

    [Fact]
    public void A_weak_key_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new PlanSigner(new byte[16], TimeProvider.System));
    }

    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }
}
