using KopCashDesk.Core;
using Microsoft.Data.Sqlite;

namespace KopCashDesk.Data;

public static class FiscalImportExtensions
{
    public static IReadOnlyList<RegisterBinding> RegisterBindings(this Database database)
    {
        using var db = Open(database);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id,organization_id,location_id,fn,register_number FROM register_bindings ORDER BY fn";
        using var reader = command.ExecuteReader();
        var result = new List<RegisterBinding>();
        while (reader.Read())
        {
            result.Add(new RegisterBinding(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4)));
        }
        return result;
    }

    public static void SaveRegisterBinding(this Database database, RegisterBinding binding)
    {
        using var db = Open(database);
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO register_bindings(id,organization_id,location_id,fn,register_number)
            VALUES($id,$org,$loc,$fn,$rn)
            ON CONFLICT(organization_id,fn) DO UPDATE SET
                location_id=excluded.location_id,
                register_number=excluded.register_number
            """;
        command.Parameters.AddWithValue("$id", binding.Id.ToString());
        command.Parameters.AddWithValue("$org", binding.OrganizationId.ToString());
        command.Parameters.AddWithValue("$loc", binding.LocationId.ToString());
        command.Parameters.AddWithValue("$fn", binding.FiscalDriveNumber.Trim());
        command.Parameters.AddWithValue("$rn", binding.RegisterNumber.Trim());
        command.ExecuteNonQuery();
    }

    public static bool UpsertFiscalOperation(this Database database, CashOperation operation)
    {
        using var db = Open(database);
        using var transaction = db.BeginTransaction();

        var existed = false;
        using (var exists = db.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT EXISTS(SELECT 1 FROM operations WHERE source=$source AND external_id=$external)";
            exists.Parameters.AddWithValue("$source", operation.Source);
            exists.Parameters.AddWithValue("$external", operation.ExternalId);
            existed = Convert.ToInt64(exists.ExecuteScalar()) != 0;
        }

        using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO operations(id,source,external_id,organization_id,location_id,occurred_at,source_kind,kind,payment,amount_kopecks,document_id)
                VALUES($id,$source,$external,$org,$loc,$time,$sourceKind,$kind,$payment,$amount,$document)
                ON CONFLICT(source,external_id) DO UPDATE SET
                    organization_id=excluded.organization_id,
                    location_id=excluded.location_id,
                    occurred_at=excluded.occurred_at,
                    source_kind=excluded.source_kind,
                    kind=excluded.kind,
                    payment=excluded.payment,
                    amount_kopecks=excluded.amount_kopecks,
                    document_id=excluded.document_id
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("$source", operation.Source);
            command.Parameters.AddWithValue("$external", operation.ExternalId);
            command.Parameters.AddWithValue("$org", operation.OrganizationId.ToString());
            command.Parameters.AddWithValue("$loc", (object?)operation.LocationId?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$time", operation.OccurredAt.ToString("O"));
            command.Parameters.AddWithValue("$sourceKind", operation.SourceKind.ToString());
            command.Parameters.AddWithValue("$kind", operation.Kind.ToString());
            command.Parameters.AddWithValue("$payment", operation.Payment.ToString());
            command.Parameters.AddWithValue("$amount", Money.ToKopecks(operation.Amount));
            command.Parameters.AddWithValue("$document", (object?)operation.SourceDocumentId ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return !existed;
    }

    private static SqliteConnection Open(Database database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString());
        connection.Open();
        return connection;
    }
}
