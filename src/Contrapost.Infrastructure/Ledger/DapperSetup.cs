using System.Data;
using Dapper;

namespace Contrapost.Infrastructure.Ledger;

internal static class DapperSetup
{
    private static int _configured;

    /// <summary>Dapper's settings are process-wide, so this runs once however many hosts start.</summary>
    public static void Configure()
    {
        if (Interlocked.Exchange(ref _configured, 1) == 1)
        {
            return;
        }

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
    }

    /// <summary>Npgsql reads timestamptz as a UTC <see cref="DateTime"/>; the views use <see cref="DateTimeOffset"/>.</summary>
    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTimeOffset offset => offset,
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => throw new DataException($"Cannot convert {value.GetType()} to DateTimeOffset."),
        };

        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value) => parameter.Value = value;
    }
}
