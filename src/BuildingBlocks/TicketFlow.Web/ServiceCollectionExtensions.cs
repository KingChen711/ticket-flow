using Microsoft.AspNetCore.Authentication;
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

        // Only services that called AddTicketFlowAuth register IAuthenticationSchemeProvider —
        // UseAuthorization() throws at startup without a matching AddAuthorization() call, so
        // this stays opt-in per service instead of running unconditionally for everyone.
        if (app.Services.GetService<IAuthenticationSchemeProvider>() is not null)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        app.MapHealthChecks("/health");
        return app;
    }
}
