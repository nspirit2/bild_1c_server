using BuildSystem.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace BuildSystem.Infrastructure;

public static class InfrastructureRegistration
{
    public static IServiceCollection AddBuildSystemInfrastructure(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContext<BuildSystemDbContext>(options => options.UseSqlServer(connectionString,
            sql => sql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorNumbersToAdd: [51001])));
        services.AddScoped<IEnvironmentOrders, SqlEnvironmentOrders>();
        services.AddScoped<IAgentControl, SqlAgentControl>();
        services.AddScoped<IArchiveCatalog, SqlArchiveCatalog>();
        services.AddScoped<IArtifactReceipts, SqlArtifactReceipts>();
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(services, new ArchiveStageOptions());
        services.AddScoped<IArchiveStageControl, SqlArchiveStageControl>();
        services.AddScoped<IStageCompletion, SqlStageCompletion>();
        services.AddScoped<IStageStatus, SqlStageStatus>();
        return services;
    }
}

// Design-time only. Migrations generate a model without requiring runtime credentials.
public sealed class BuildSystemDesignTimeFactory : IDesignTimeDbContextFactory<BuildSystemDbContext>
{
    public BuildSystemDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("BUILDSYSTEM_DESIGN_CONNECTION")
            ?? throw new InvalidOperationException("Set BUILDSYSTEM_DESIGN_CONNECTION explicitly for EF tooling.");
        return new BuildSystemDbContext(new DbContextOptionsBuilder<BuildSystemDbContext>()
            .UseSqlServer(connection).Options);
    }
}
