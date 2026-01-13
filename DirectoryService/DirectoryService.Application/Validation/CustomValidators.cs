using System.Text.Json;
using CSharpFunctionalExtensions;
using FluentValidation;
using Shared;

namespace DirectoryService.Application.Validation;

public static class CustomValidators
{
    public static IRuleBuilderOptionsConditions<T, TElement> MustBeValueObject<T, TElement, TVelueObject>(
        this IRuleBuilder<T, TElement> ruleBuilder, Func<TElement, Result<TVelueObject, Error>> factoryMethod)
    {
        return ruleBuilder.Custom((value, context) =>
        {
            Result<TVelueObject, Error> result = factoryMethod.Invoke(value);

            if (result.IsSuccess)
                return;

            context.AddFailure(JsonSerializer.Serialize(result.Error));
        });
    }

    public static IRuleBuilderOptions<T, TProperty> WithErrors<T, TProperty>(
        this IRuleBuilderOptions<T, TProperty> rule, Error error)
    {
        return rule.WithMessage(JsonSerializer.Serialize(error));
    }
}