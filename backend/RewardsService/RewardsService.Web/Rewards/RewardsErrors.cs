using Shared;

namespace RewardsService.Web.Rewards;

public static class RewardsErrors
{
    public static Error AmountMustBePositive() =>
        Error.Validation("rewards.amount.must_be_positive", "Amount must be greater than zero", "amount");

    public static Error ReasonRequired() =>
        Error.Validation("rewards.reason.required", "Reason is required", "reason");

    public static Error IdempotencyKeyRequired() =>
        Error.Validation(
            "rewards.idempotency_key.required",
            "Idempotency-Key header is required",
            "Idempotency-Key");

    public static Error AgentGrantOverLimit(decimal max) =>
        Error.Validation(
            "rewards.agent_grant.over_limit", $"An assistant may grant at most {max} at a time", "amount");

    public static Error AgentQuotaExceeded(decimal max) =>
        Error.Validation(
            "rewards.agent_grant.over_quota", $"An assistant may grant at most {max} per day for you", "amount");

    public static Error CannotGrantToSelf() =>
        Error.Authorization("rewards.grant.self", "You cannot grant currency to yourself", "employeeId");

    // Same key reused with a different request body - not a retry, a caller bug (or, worst
    // case, two different callers colliding on a client-generated key). 422, per the IETF
    // Idempotency-Key draft, not a silent "here's the first grant's result".
    public static Error IdempotencyKeyReused() =>
        Error.Unprocessable(
            "rewards.idempotency_key.reused",
            "Idempotency-Key was already used with a different request");
}
