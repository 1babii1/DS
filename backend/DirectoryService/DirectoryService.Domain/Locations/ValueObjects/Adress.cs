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
        var errors = new List<ErrorMessage>();

        // Верхние границы обязаны совпадать с HasMaxLength в LocationConfigurations:
        // без них слишком длинный адрес проходит валидацию и падает уже на вставке,
        // превращая ошибку ввода в 500.
        if (string.IsNullOrWhiteSpace(street))
        {
            errors.Add(new ErrorMessage("value.is.required", "Street is required", nameof(Street)));
        }
        else if (street.Length is < LengthConstants.MinTextLength or > LengthConstants.MaxStreetLength)
        {
            errors.Add(GeneralErrors.LengthIsInvalid(
                nameof(Street), LengthConstants.MinTextLength, LengthConstants.MaxStreetLength).Messages[0]);
        }

        if (string.IsNullOrWhiteSpace(city))
        {
            errors.Add(new ErrorMessage("value.is.required", "City is required", nameof(City)));
        }
        else if (city.Length is < LengthConstants.MinTextLength or > LengthConstants.MaxCityLength)
        {
            errors.Add(GeneralErrors.LengthIsInvalid(
                nameof(City), LengthConstants.MinTextLength, LengthConstants.MaxCityLength).Messages[0]);
        }

        if (string.IsNullOrWhiteSpace(country))
        {
            errors.Add(new ErrorMessage("value.is.required", "Country is required", nameof(Country)));
        }
        else if (country.Length is < LengthConstants.MinTextLength or > LengthConstants.MaxCountryLength)
        {
            errors.Add(GeneralErrors.LengthIsInvalid(
                nameof(Country), LengthConstants.MinTextLength, LengthConstants.MaxCountryLength).Messages[0]);
        }

        if (errors.Any())
            return Result.Failure<Address, Error>(Error.Validation(errors));

        Address adress = new(street, city, country);

        return Result.Success<Address, Error>(adress);
    }
}