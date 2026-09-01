using LakeWright.Multitenancy;
using Microsoft.EntityFrameworkCore;

const string connectionVariable = "LAKEWRIGHT_MIGRATION_CONNECTION_STRING";

if (args.Length != 1 || args[0] is "--help" or "-h")
{
    Console.Error.WriteLine("Usage: LakeWright.DatabaseMaintenance <migrate|validate|finalize|rollback|maintain>");
    Console.Error.WriteLine($"Set {connectionVariable} to the table-owning migration-role connection string.");
    return args.Length == 1 ? 0 : 2;
}

var connectionString = Environment.GetEnvironmentVariable(connectionVariable);
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine($"{connectionVariable} is required. The application connection string is not accepted.");
    return 2;
}

if (!int.TryParse(Environment.GetEnvironmentVariable("LAKEWRIGHT_AUDIT_RETENTION_YEARS") ?? "7", out var retentionYears)
    || !int.TryParse(Environment.GetEnvironmentVariable("LAKEWRIGHT_AUDIT_FUTURE_MONTHS") ?? "2", out var futureMonths))
{
    Console.Error.WriteLine("Audit retention and future-month settings must be whole numbers.");
    return 2;
}

var options = new AuditPartitionOptions
{
    RetentionYears = retentionYears,
    FutureMonths = futureMonths
};
var dbOptions = new DbContextOptionsBuilder<LakeWrightDbContext>()
    .UseNpgsql(connectionString)
    .Options;
await using var db = new LakeWrightDbContext(dbOptions);
var now = DateTimeOffset.UtcNow;

switch (args[0])
{
    case "migrate":
        await DatabasePartitioning.MigrateAsync(db, now, options);
        await DatabasePartitioning.ValidateAsync(db);
        Console.WriteLine("Audit partition migration is valid. Run finalize after the deployment smoke check, or rollback.");
        break;
    case "validate":
        await DatabasePartitioning.ValidateAsync(db);
        Console.WriteLine("Audit partition validation passed.");
        break;
    case "finalize":
        await DatabasePartitioning.FinalizeMigrationAsync(db);
        Console.WriteLine("Audit partition rollback copy finalized.");
        break;
    case "rollback":
        await DatabasePartitioning.RollbackMigrationAsync(db);
        Console.WriteLine("Audit partition migration rolled back; the partitioned copy remains for inspection.");
        break;
    case "maintain":
        var result = await DatabasePartitioning.MaintainAsync(db, now, options);
        Console.WriteLine($"Audit partitions maintained: {result.CreatedPartitions} created, {result.DroppedPartitions} dropped.");
        break;
    default:
        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
        return 2;
}

return 0;
