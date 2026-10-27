using Encore.Shared;
using Encore.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Passes;

public static class PassesModule
{
    public static IServiceCollection AddPassesModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Encore") ?? throw new InvalidOperationException("Connection string 'Encore' is not configured.");
        services.AddDbContext<PassesDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", PassesDbContext.SchemaName)));
        services.AddScoped<PassesHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<PassesDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<PassesDbContext>>("passes-db", tags: ["ready"]);

        messaging.AddOutbox<PassesDbContext>()
            .AddConsumer<PassesHandler>(Topology.PassesQueue, [Topology.HeldKey], prefetch: 4);
        return services;
    }
}
