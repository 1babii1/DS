using System.Text.Json;
using FluentValidation.Results;
using Shared;

namespace DirectoryService.Application.Validation;

public static class ValidationExtentions
{
    // public static Error ToError(this ValidationResult validationResult)
    // {
    //     List<ValidationFailure> validationFailures = validationResult.Errors;
    //
    //     var errors = from validationFailure in validationFailures
    //         let errorMessage = validationFailure.ErrorMessage
    //         let error = JsonSerializer.Deserialize<Error>(errorMessage)
    //         select error.Messages;
    //
    //     return Error.Validation(errors.SelectMany(e => e));
    // }
    public static Error ToError(this ValidationResult validationResult)
    {
        var errorMessages = validationResult.Errors.Select(error =>
        {
            var field = error.PropertyName ?? "validation";
            return new ErrorMessages(field, error.ErrorMessage, field);
        });

        return Error.Validation(errorMessages);
    }
}