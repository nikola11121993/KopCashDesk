using Microsoft.Data.Sqlite;
using System.Globalization;

namespace KopCashDesk.Data;

public sealed record RegisterFiscalDriveHistory(
    Guid OrganizationId,
    string KktSerial,
    string RegisterNumber,
    string FiscalDriveNumber,
    DateTimeOffset? FirstSeen,
    DateTimeOffset? LastSeen);

public static class RegisterFiscalDriveHistoryExtensions
{
    public static IReadOnlyList<RegisterFiscalDriveHistory> RegisterFiscalDriveHistory(
        this Database database,
        Guid? organizationId = null)
    {
        using var db = RegisterBindingService.Open(database);
        using var command = db.CreateCommand();
        command.CommandText = """
            WITH observed AS (
                SELECT organization_id,kkt_serial,registration_number,fn,occurred_at AS seen_at
                FROM operations
                WHERE source_kind<>'Bank' AND fn<>''
                UNION ALL
                SELECT organization_id,kkt_serial,registration_number,fn,closed_at
                FROM shift_closures
                WHERE fn<>''
                UNION ALL
                SELECT organization_id,kkt_serial,register_number,fn,created_at
                FROM register_bindings
                WHERE fn<>''
            )
            SELECT organization_id,kkt_serial,registration_number,fn,MIN(seen_at),MAX(seen_at)
            FROM observed
            WHERE ($org IS NULL OR organization_id=$org)
            GROUP BY organization_id,kkt_serial,registration_number,fn
            ORDER BY organization_id,kkt_serial,registration_number,MIN(seen_at),fn;
            """;
        command.Parameters.AddWithValue("$org", organizationId is null ? DBNull.Value : organizationId.Value.ToString());

        using var reader = command.ExecuteReader();
        var result = new List<RegisterFiscalDriveHistory>();
        while (reader.Read())
        {
            result.Add(new(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                ParseNullable(reader, 4),
                ParseNullable(reader, 5)));
        }
        return result;
    }

    private static DateTimeOffset? ParseNullable(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) || string.IsNullOrWhiteSpace(reader.GetString(ordinal))
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);
}
