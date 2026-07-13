using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace TicketFlow.Web;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTicketFlowWeb(this IServiceCollection services)
    {
        services.AddHealthChecks();
        return services;
    }

    public static WebApplication UseTicketFlowWeb(this WebApplication app)
    {
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        app.MapHealthChecks("/health");
        return app;
    }
}
