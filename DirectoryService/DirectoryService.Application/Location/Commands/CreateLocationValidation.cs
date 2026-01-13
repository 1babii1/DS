using DirectoryService.Application.Validation;
using DirectoryService.Contracts.Request.Location;
using DirectoryService.Domain.Locations.ValueObjects;
using FluentValidation;
using Address = DirectoryService.Domain.Locations.ValueObjects.Address;

namespace DirectoryService.Application.Location.Commands;

public class CreateLocationValidation : AbstractValidator<CreateLocationRequest>
{
    public CreateLocationValidation()
    {
        RuleFor(x => x.Name).MustBeValueObject(LocationName.Create);

        RuleFor(x => x.Address)
            .MustBeValueObject(address => Address.Create(address.Street, address.City, address.Country));

        RuleFor(x => x.Timezone).MustBeValueObject(Timezone.Create);
    }
}