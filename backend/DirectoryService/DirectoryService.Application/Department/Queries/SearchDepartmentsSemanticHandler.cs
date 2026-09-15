using CSharpFunctionalExtensions;
using DirectoryService.Application.Search;
using DirectoryService.Application.Validation;
using DirectoryService.Contracts.Response.Department;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;
using Shared;

namespace DirectoryService.Application.Department.Queries;

public record SearchDepartmentsSemanticRequest(string Query, int Limit = 10);

public class SearchDepartmentsSemanticValidator : AbstractValidator<SearchDepartmentsSemanticRequest>
{
    public SearchDepartmentsSemanticValidator()
    {
        RuleFor(x => x.Query).NotEmpty().WithMessage("query cannot be empty");
        RuleFor(x => x.Limit).InclusiveBetween(1, 50).WithMessage("limit must be between 1 and 50");
    }
}

public class SearchDepartmentsSemanticHandler(
    IDepartmentSemanticSearch search,
    ILogger<SearchDepartmentsSemanticHandler> logger,
    SearchDepartmentsSemanticValidator validator)
{
    public async Task<Result<List<DepartmentSearchResultDto>, Error>> Handle(
        SearchDepartmentsSemanticRequest request,
        CancellationToken cancellationToken)
    {
        ValidationResult validationResult = await validator.ValidateAsync(request, cancellationToken);
        if (!validationResult.IsValid)
        {
            logger.LogError("Failed to validate semantic search request");
            return validationResult.ToError();
        }

        return await search.SearchAsync(request.Query, request.Limit, cancellationToken);
    }
}