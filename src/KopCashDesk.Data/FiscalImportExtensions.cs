using KopCashDesk.Core;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace KopCashDesk.Data;

public static class FiscalImportExtensions
{
    public static IReadOnlyList<RegisterBinding> RegisterBindings(this Database database)
    {
        using var db = Open(database);
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT id,organization_id,location_id,fn,register_number,binding_source,is_locked,valid_from,valid_to
            FROM register_bindings
            ORDER BY fn
            """;
        using var reader = command.ExecuteReader();
        var result = new List<RegisterBinding>();
        while (reader.Read())
        {
            result.Add(new RegisterBinding(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                ParseSource(reader.GetString(5)),
                reader.GetInt64(6) != 0,
                reader.IsDBNull(7) ? null : DateOnly.ParseExact(reader.GetString(7), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.IsDBNull(8) ? null : DateOnly.ParseExact(reader.GetString(8), "yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }
        return result;
    }

    public static void SaveRegisterBinding(this Database database, RegisterBinding binding)
    {
        using var db = Open(database);
        using var transaction = db.BeginTransaction();

        RegisterBinding? existing = null;
        using (var read = db.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT id,organization_id,location_id,fn,register_number,binding_source,is_locked,valid_from,valid_to
                FROM register_bindings
                WHERE organization_id=$org AND fn=$fn
                LIMIT 1
                """;
            read.Parameters.AddWithValue("$org", binding.OrganizationId.ToString());
            read.Parameters.AddWithValue("$fn", binding.FiscalDriveNumber.Trim());
            using var reader = read.ExecuteReader();
            if (reader.Read())
            {
                existing = new RegisterBinding(
                    Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
                    reader.GetString(3), reader.GetString(4), ParseSource(reader.GetString(5)), reader.GetInt64(6) != 0,
                    reader.IsDBNull(7) ? null : DateOnly.ParseExact(reader.GetString(7), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    reader.IsDBNull(8) ? null : DateOnly.ParseExact(reader.GetString(8), "yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
        }

        if (existing is not null &&
            (existing.IsLocked && binding.BindingSource != BindingSource.Manual || Rank(existing.BindingSource) > Rank(binding.BindingSource)))
        {
            transaction.Rollback();
            return;
        }

        using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO register_bindings(id,organization_id,location_id,fn,register_number,binding_source,is_locked,valid_from,valid_to)
                VALUES($id,$org,$loc,$fn,$rn,$source,$locked,$from,$to)
                ON CONFLICT(organization_id,fn) DO UPDATE SET
                    location_id=excluded.location_id,
                    register_number=excluded.register_number,
                    binding_source=excluded.binding_source,
                    is_locked=excluded.is_locked,
                    valid_from=excluded.valid_from,
                    valid_to=excluded.valid_to
                """;
            command.Parameters.AddWithValue("$id", existing?.Id.ToString() ?? binding.Id.ToString());
            command.Parameters.AddWithValue("$org", binding.OrganizationId.ToString());
            command.Parameters.AddWithValue("$loc", binding.LocationId.ToString());
            command.Parameters.AddWithValue("$fn", binding.FiscalDriveNumber.Trim());
            command.Parameters.AddWithValue("$rn", binding.RegisterNumber.Trim());
            command.Parameters.AddWithValue("$source", binding.BindingSource.ToString());
            command.Parameters.AddWithValue("$locked", binding.IsLocked ? 1 : 0);
            command.Parameters.AddWithValue("$from", binding.ValidFrom is null ? DBNull.Value : binding.ValidFrom.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$to", binding.ValidTo is null ? DBNull.Value : binding.ValidTo.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        if (existing is not null && (existing.LocationId != binding.LocationId || existing.BindingSource != binding.BindingSource || existing.IsLocked != binding.IsLocked))
        {
            Audit(db, transaction, "register.rebind",
                $"fn={binding.FiscalDriveNumber}; old_loc={existing.LocationId}; new_loc={binding.LocationId}; old_source={existing.BindingSource}; new_source={binding.BindingSource}; locked={binding.IsLocked}");
        }

        transaction.Commit();
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
