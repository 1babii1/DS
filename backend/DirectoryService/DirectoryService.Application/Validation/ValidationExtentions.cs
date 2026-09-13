using FluentValidation.Results;
using Shared;

namespace DirectoryService.Application.Validation;

public static class ValidationExtentions
{
    public static Error ToError(this ValidationResult validationResult)
    {
        var errorMessages = validationResult.Errors.Select(error =>
        {
            var field = error.PropertyName ?? "validation";
            return new ErrorMessage(field, error.ErrorMessage, field);
        });

        return Error.Validation(errorMessages);
    }
}