using Encore.Shared;
using Encore.Shared.Contracts;
using Encore.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Encore.Gigs;

public static class GigsModule
{
    public static IServiceCollection AddGigsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Encore") ?? throw new InvalidOperationException("Connection string 'Encore' is not configured.");
        services.AddDbContext<GigsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", GigsDbContext.SchemaName)));
        services.AddScoped<GigsHandler>();
        services.AddScoped<IGigCatalog, GigCatalog>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<GigsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<GigsDbContext>>("seats-db", tags: ["ready"]);

        messaging.AddOutbox<GigsDbContext>()
            .AddConsumer<GigsHandler>(Topology.GigsQueue, [Topology.ReserveKey, Topology.ReleaseKey], prefetch: 1);
        return services;
    }
}
