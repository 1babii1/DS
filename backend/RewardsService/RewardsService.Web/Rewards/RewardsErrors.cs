using Shared;

namespace RewardsService.Web.Rewards;

public static class RewardsErrors
{
    public static Error AmountMustBePositive() =>
        Error.Validation("rewards.amount.must_be_positive", "Amount must be greater than zero", "amount");

    public static Error ReasonRequired() =>
        Error.Validation("rewards.reason.required", "Reason is required", "reason");
}
