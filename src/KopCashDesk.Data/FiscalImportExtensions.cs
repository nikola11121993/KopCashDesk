using KopCashDesk.Core;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace KopCashDesk.Data;

public static class FiscalImportExtensions
{
    public static IReadOnlyList<RegisterBinding> RegisterBindings(this Database database)
        => RegisterBindingService.Read(database);

    public static void SaveRegisterBinding(this Database database, RegisterBinding binding)
        => RegisterBindingService.Save(database, binding);

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
                INSERT INTO operations(id,source,external_id,organization_id,location_id,occurred_at,source_kind,kind,payment,amount_kopecks,document_id,fn,kkt_serial,registration_number,register_display_name,shift_number)
                VALUES($id,$source,$external,$org,$loc,$time,$sourceKind,$kind,$payment,$amount,$document,$fn,$serial,$rnm,$name,$shift)
                ON CONFLICT(source,external_id) DO UPDATE SET
                    organization_id=excluded.organization_id,
                    location_id=excluded.location_id,
                    occurred_at=excluded.occurred_at,
                    source_kind=excluded.source_kind,
                    kind=excluded.kind,
                    payment=excluded.payment,
                    amount_kopecks=excluded.amount_kopecks,
                    document_id=excluded.document_id,
                    fn=excluded.fn,kkt_serial=excluded.kkt_serial,registration_number=excluded.registration_number,
                    register_display_name=excluded.register_display_name,shift_number=excluded.shift_number
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
            command.Parameters.AddWithValue("$fn", operation.FiscalDriveNumber);
            command.Parameters.AddWithValue("$serial", operation.KktSerial);
            command.Parameters.AddWithValue("$rnm", operation.RegistrationNumber);
            command.Parameters.AddWithValue("$name", operation.RegisterDisplayName);
            command.Parameters.AddWithValue("$shift", (object?)operation.ShiftNumber ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return !existed;
    }

    private static void Audit(SqliteConnection db, SqliteTransaction tx, string action, string details)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO audit_log(occurred_at,action,details) VALUES($t,$a,$d)";
        command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$a", action);
        command.Parameters.AddWithValue("$d", details);
        command.ExecuteNonQuery();
    }

    private static BindingSource ParseSource(string value) =>
        Enum.TryParse<BindingSource>(value, true, out var source) ? source : BindingSource.Automatic;

    private static int Rank(BindingSource source) => source switch
    {
        BindingSource.Manual => 3,
        BindingSource.Rule => 2,
        _ => 1
    };

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
