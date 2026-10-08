using EmployeeService.Application.Employees;
using Temporalio.Extensions.Hosting;

namespace EmployeeService.Web.Temporal;

public static class HireOrchestrationRegistration
{
    /// <summary>
    /// Chooses what carries a hire through its onboarding (<c>HireOrchestration:Mode</c>, ADR 0056): the saga of ADR 0032, which is the default,
    /// or the Temporal workflow, which also runs a worker in this process and a reconciler. Nothing of Temporal is registered in saga mode.
    /// </summary>
    public static IServiceCollection AddHireOrchestration(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(HireOrchestrationOptions.SectionName);
        services.Configure<HireOrchestrationOptions>(section);
        var options = section.Get<HireOrchestrationOptions>() ?? new HireOrchestrationOptions();

        services.AddScoped<SagaHireOrchestrator>();

        if (options.Mode != HireOrchestrationMode.Temporal)
        {
            services.AddScoped<IHireOrchestrator>(provider => provider.GetRequiredService<SagaHireOrchestrator>());
            return services;
        }

        services.AddTemporalClient(options.Address, options.Namespace);
        services
            .AddHostedTemporalWorker(options.Address, options.Namespace, options.TaskQueue)
            .AddWorkflow<HireWorkflow>()
            .AddScopedActivities<HireActivities>();

        services.AddScoped<TemporalHireOrchestrator>();
        services.AddScoped<IHireOrchestrator>(provider => provider.GetRequiredService<TemporalHireOrchestrator>());
        services.AddHostedService<TemporalHireReconciler>();

        return services;
    }
}
