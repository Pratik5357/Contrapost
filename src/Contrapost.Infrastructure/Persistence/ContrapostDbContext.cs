using Microsoft.EntityFrameworkCore;

namespace Contrapost.Infrastructure.Persistence;

/// <summary>
/// EF Core is used for migrations and simple reads only; the write path uses
/// hand-written SQL (design doc §3). The schema itself comes from db/schema.sql,
/// so entities mapped here must be excluded from migrations.
/// </summary>
public sealed class ContrapostDbContext(DbContextOptions<ContrapostDbContext> options) : DbContext(options);
