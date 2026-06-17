using Microsoft.EntityFrameworkCore;

namespace Veil.DbMigrator;

/// <summary>
/// One module's DbContext to migrate. <paramref name="Factory"/> constructs a
/// fresh context (the argument is unused — present only so the lambda reads
/// uniformly at the call site).
/// </summary>
internal sealed record MigrationStep(string Name, Func<object?, DbContext> Factory);
