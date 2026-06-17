using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Veil.Auth.Infrastructure.Persistence;
using Veil.Certificates.Infrastructure.Persistence;
using Veil.DbMigrator;
using Veil.EdgeNodes.Infrastructure.Persistence;
using Veil.Infrastructure.Security;
using Veil.Zones.Infrastructure.Persistence;

// Standalone DB migrator/seeder. Applies the EF Core migrations that already
// ship inside each module assembly to the target database, then runs any
// seed routines. Generating new migration *files* stays a design-time job
// (dotnet ef migrations add) — see migrate.ps1 / README in this folder.

IConfiguration configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables(prefix: "VEIL_")
    .AddCommandLine(args)
    .Build();

// Precedence: --connection / ConnectionStrings:Default (config) → env
// VEIL_ConnectionStrings__Default → dev default.
string connectionString =
    configuration["connection"]
    ?? configuration.GetConnectionString("Default")
    ?? "Host=localhost;Port=5432;Database=veil;Username=veil;Password=veil";

Console.WriteLine($"Veil DB migrator → {Redact(connectionString)}");
Console.WriteLine();

// One entry per module DbContext. Each gets the same connection string; the
// HasDefaultSchema in OnModelCreating keeps the modules isolated by schema.
MigrationStep[] steps =
[
    new("Security",     o => new SecurityDbContext(Build<SecurityDbContext>(o, connectionString))),
    new("Auth",         o => new AuthDbContext(Build<AuthDbContext>(o, connectionString))),
    new("Zones",        o => new ZonesDbContext(Build<ZonesDbContext>(o, connectionString))),
    new("Certificates", o => new CertificatesDbContext(Build<CertificatesDbContext>(o, connectionString))),
    new("EdgeNodes",    o => new EdgeNodesDbContext(Build<EdgeNodesDbContext>(o, connectionString))),
];

int failures = 0;
foreach (MigrationStep step in steps)
{
    try
    {
        await using DbContext db = step.Factory(null);

        IEnumerable<string> pending = await db.Database.GetPendingMigrationsAsync();
        string[] pendingList = pending.ToArray();

        if (pendingList.Length == 0)
        {
            Console.WriteLine($"[{step.Name,-12}] up to date — no pending migrations.");
            continue;
        }

        Console.WriteLine($"[{step.Name,-12}] applying {pendingList.Length} migration(s):");
        foreach (string migration in pendingList)
            Console.WriteLine($"               · {migration}");

        await db.Database.MigrateAsync();
        Console.WriteLine($"[{step.Name,-12}] done.");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"[{step.Name,-12}] FAILED: {ex.Message}");
    }
}

Console.WriteLine();
if (failures > 0)
{
    Console.Error.WriteLine($"Completed with {failures} failure(s).");
    return 1;
}

Console.WriteLine("All module databases migrated successfully.");
return 0;

// Builds typed DbContextOptions pointing at Npgsql. The migrations assembly
// defaults to the context's own assembly, which is exactly where each
// module keeps its migrations — no extra wiring needed.
static DbContextOptions<TContext> Build<TContext>(object? _, string connection)
    where TContext : DbContext =>
    new DbContextOptionsBuilder<TContext>()
        .UseNpgsql(connection)
        .Options;

static string Redact(string connection)
{
    string[] parts = connection.Split(';', StringSplitOptions.RemoveEmptyEntries);
    return string.Join(';', parts.Select(p =>
        p.TrimStart().StartsWith("Password", StringComparison.OrdinalIgnoreCase)
            ? "Password=***"
            : p));
}
