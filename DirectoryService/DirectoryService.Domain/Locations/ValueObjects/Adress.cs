using CSharpFunctionalExtensions;
using Shared;

namespace DirectoryService.Domain.Locations.ValueObjects;

public record Address
{
    public string Street { get; }
    public string City { get; }
    public string Country { get; }

    private Address(string street, string city, string country)
    {
        Street = street;
        City = city;
        Country = country;
    }

    public static Result<Address, Error> Create(string street, string city, string country)
    {
        var errors = new List<ErrorMessages>();

        if(string.IsNullOrWhiteSpace(street))
            errors.Add(new ErrorMessages(null!, "Street is required"));
        else if(street.Length < LenghtConstants.LENGTH3)
            errors.Add(new ErrorMessages("length.is.invalid", "Location street cannot be less than 3 characters"));

        if(string.IsNullOrWhiteSpace(city))
            errors.Add(new ErrorMessages(null!, "City is required"));
        else if(city.Length < LenghtConstants.LENGTH3)
            errors.Add(new ErrorMessages("length.is.invalid", "Location city cannot be less than 3 characters"));

        if(string.IsNullOrWhiteSpace(country))
            errors.Add(new ErrorMessages(null!, "Country is required"));
        else if(country.Length < LenghtConstants.LENGTH3)
            errors.Add(new ErrorMessages("length.is.invalid", "Location country cannot be less than 3 characters"));

        if(errors.Any())
            return Result.Failure<Address, Error>(Error.Validation(errors));

        Address adress = new(street, city, country);

        return Result.Success<Address, Error>(adress);
    }
}