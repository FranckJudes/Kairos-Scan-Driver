using System.Data;
using Dapper;

namespace OpenScan.Persistence;


internal static class DapperTypeHandlers
{
    private static bool _registered;

    public static void EnsureRegistered()
    {
        if (_registered)
        {
            return;
        }

        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
        SqlMapper.AddTypeHandler(new NullableDateTimeOffsetHandler());
        _registered = true;
    }

    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value) =>
            parameter.Value = value.ToString("O");

        public override DateTimeOffset Parse(object value) => DateTimeOffset.Parse((string)value);
    }

    private sealed class NullableDateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset?>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset? value) =>
            parameter.Value = value.HasValue ? value.Value.ToString("O") : DBNull.Value;

        public override DateTimeOffset? Parse(object value) => value is DBNull ? null : DateTimeOffset.Parse((string)value);
    }
}
