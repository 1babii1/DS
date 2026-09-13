namespace Shared;

/// <summary>
/// Длины текстовых полей. Каждое значение используется дважды - в доменной валидации и
/// в конфигурации EF, - поэтому имя описывает правило, а не число: при именах вида
/// LENGTH100 расхождение между проверкой и схемой не видно в коде.
/// </summary>
public static class LengthConstants
{
    public const int MinTextLength = 3;

    public const int MaxDepartmentNameLength = 150;

    public const int MaxDepartmentIdentifierLength = 150;

    public const int MaxPositionNameLength = 100;

    public const int MaxPositionDescriptionLength = 1000;

    public const int MaxLocationNameLength = 120;

    public const int MaxStreetLength = 100;

    public const int MaxCityLength = 60;

    public const int MaxCountryLength = 60;
}
