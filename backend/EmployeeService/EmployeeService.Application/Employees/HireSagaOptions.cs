namespace EmployeeService.Application.Employees;

public sealed class HireSagaOptions
{
    public const string SectionName = "HireSaga";

    /// <summary>How long a hire has to get its login account and its welcome bonus before the process undoes what it did.</summary>
    public TimeSpan OnboardingTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
