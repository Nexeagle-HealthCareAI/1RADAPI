using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace _1Rad.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly()));
        services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());
        services.AddAutoMapper(Assembly.GetExecutingAssembly());
        services.AddScoped<_1Rad.Application.Features.Finance.Queries.GetInvoices.IInvoiceEnrichmentService, _1Rad.Application.Features.Finance.Queries.GetInvoices.InvoiceEnrichmentService>();
        
        return services;
    }
}
