using KopCashDesk.Core;
using Microsoft.Data.Sqlite;

namespace KopCashDesk.Data;

public readonly record struct FiscalBatchResult(int Inserted, int Updated);
public readonly record struct BankBatchResult(int Inserted, int Updated);

public static class DatabaseBulkExtensions
{
    public static bool UpsertBankOperation(this Database database, CashOperation operation)
    {
        using var db = Open(database);
        using var transaction = db.BeginTransaction();

        using var exists = db.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT EXISTS(SELECT 1 FROM operations WHERE source=$source AND external_id=$external)";
        exists.Parameters.AddWithValue("$source", operation.Source);
        exists.Parameters.AddWithValue("$external", operation.ExternalId);
        var existed = Convert.ToInt64(exists.ExecuteScalar()) != 0;

        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO operations(
                id,source,external_id,organization_id,location_id,occurred_at,source_kind,kind,payment,amount_kopecks,document_id)
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

        transaction.Commit();
        return !existed;
    }

    public static BankBatchResult UpsertBankOperationsBatch(this Database database, IReadOnlyCollection<CashOperation> operations)
    {
        if (operations.Count == 0) return new BankBatchResult(0, 0);

        using var db = Open(database);
        using var transaction = db.BeginTransaction();

        using var exists = db.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT EXISTS(SELECT 1 FROM operations WHERE source=$source AND external_id=$external)";
        var existsSource = exists.Parameters.Add("$source", SqliteType.Text);
        var existsExternal = exists.Parameters.Add("$external", SqliteType.Text);

        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO operations(
                id,source,external_id,organization_id,location_id,occurred_at,source_kind,kind,payment,amount_kopecks,document_id)
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

        var id = command.Parameters.Add("$id", SqliteType.Text);
        var source = command.Parameters.Add("$source", SqliteType.Text);
        var external = command.Parameters.Add("$external", SqliteType.Text);
        var organization = command.Parameters.Add("$org", SqliteType.Text);
        var location = command.Parameters.Add("$loc", SqliteType.Text);
        var time = command.Parameters.Add("$time", SqliteType.Text);
        var sourceKind = command.Parameters.Add("$sourceKind", SqliteType.Text);
        var kind = command.Parameters.Add("$kind", SqliteType.Text);
        var payment = command.Parameters.Add("$payment", SqliteType.Text);
        var amount = command.Parameters.Add("$amount", SqliteType.Integer);
        var document = command.Parameters.Add("$document", SqliteType.Text);

        var inserted = 0;
        var updated = 0;
        var seenInBatch = new HashSet<string>(StringComparer.Ordinal);

        foreach (var operation in operations)
        {
            var key = operation.Source + "\n" + operation.ExternalId;
            var alreadySeen = !seenInBatch.Add(key);
            var existed = alreadySeen;
            if (!alreadySeen)
            {
                existsSource.Value = operation.Source;
                existsExternal.Value = operation.ExternalId;
                existed = Convert.ToInt64(exists.ExecuteScalar()) != 0;
            }

            id.Value = Guid.NewGuid().ToString();
            source.Value = operation.Source;
            external.Value = operation.ExternalId;
            organization.Value = operation.OrganizationId.ToString();
            location.Value = (object?)operation.LocationId?.ToString() ?? DBNull.Value;
            time.Value = operation.OccurredAt.ToString("O");
            sourceKind.Value = operation.SourceKind.ToString();
            kind.Value = operation.Kind.ToString();
            payment.Value = operation.Payment.ToString();
            amount.Value = Money.ToKopecks(operation.Amount);
            document.Value = (object?)operation.SourceDocumentId ?? DBNull.Value;
            command.ExecuteNonQuery();

            if (existed) updated++;
            else inserted++;
        }

        transaction.Commit();
        return new BankBatchResult(inserted, updated);
    }

    public static int InsertOperationsBatch(this Database database, IReadOnlyCollection<CashOperation> operations)
    {
        if (operations.Count == 0) return 0;

        using var db = Open(database);
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO operations(
                id,source,external_id,organization_id,location_id,occurred_at,source_kind,kind,payment,amount_kopecks,document_id)
            VALUES($id,$source,$external,$org,$loc,$time,$sourceKind,$kind,$payment,$amount,$document)
            """;

        var id = command.Parameters.Add("$id", SqliteType.Text);
        var source = command.Parameters.Add("$source", SqliteType.Text);
        var external = command.Parameters.Add("$external", SqliteType.Text);
        var organization = command.Parameters.Add("$org", SqliteType.Text);
        var location = command.Parameters.Add("$loc", SqliteType.Text);
        var time = command.Parameters.Add("$time", SqliteType.Text);
        var sourceKind = command.Parameters.Add("$sourceKind", SqliteType.Text);
        var kind = command.Parameters.Add("$kind", SqliteType.Text);
        var payment = command.Parameters.Add("$payment", SqliteType.Text);
        var amount = command.Parameters.Add("$amount", SqliteType.Integer);
        var document = command.Parameters.Add("$document", SqliteType.Text);

        var inserted = 0;
        foreach (var operation in operations)
        {
            id.Value = Guid.NewGuid().ToString();
            source.Value = operation.Source;
            external.Value = operation.ExternalId;
            organization.Value = operation.OrganizationId.ToString();
            location.Value = (object?)operation.LocationId?.ToString() ?? DBNull.Value;
            time.Value = operation.OccurredAt.ToString("O");
            sourceKind.Value = operation.SourceKind.ToString();
            kind.Value = operation.Kind.ToString();
            payment.Value = operation.Payment.ToString();
            amount.Value = Money.ToKopecks(operation.Amount);
            document.Value = (object?)operation.SourceDocumentId ?? DBNull.Value;
            inserted += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return inserted;
    }

    public static FiscalBatchResult UpsertFiscalOperationsBatch(this Database database, IReadOnlyCollection<CashOperation> operations)
    {
        if (operations.Count == 0) return new FiscalBatchResult(0, 0);

        using var db = Open(database);
        using var transaction = db.BeginTransaction();

        using var exists = db.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT EXISTS(SELECT 1 FROM operations WHERE source=$source AND external_id=$external)";
        var existsSource = exists.Parameters.Add("$source", SqliteType.Text);
        var existsExternal = exists.Parameters.Add("$external", SqliteType.Text);

        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO operations(
                id,source,external_id,organization_id,location_id,occurred_at,source_kind,kind,payment,amount_kopecks,
                document_id,fn,kkt_serial,registration_number,register_display_name,shift_number)
            VALUES(
                $id,$source,$external,$org,$loc,$time,$sourceKind,$kind,$payment,$amount,
                $document,$fn,$serial,$rnm,$name,$shift)
            ON CONFLICT(source,external_id) DO UPDATE SET
                organization_id=excluded.organization_id,
                location_id=excluded.location_id,
                occurred_at=excluded.occurred_at,
                source_kind=excluded.source_kind,
                kind=excluded.kind,
                payment=excluded.payment,
                amount_kopecks=excluded.amount_kopecks,
                document_id=excluded.document_id,
                fn=excluded.fn,
                kkt_serial=excluded.kkt_serial,
                registration_number=excluded.registration_number,
                register_display_name=excluded.register_display_name,
                shift_number=excluded.shift_number
            """;

        var id = command.Parameters.Add("$id", SqliteType.Text);
        var source = command.Parameters.Add("$source", SqliteType.Text);
        var external = command.Parameters.Add("$external", SqliteType.Text);
        var organization = command.Parameters.Add("$org", SqliteType.Text);
        var location = command.Parameters.Add("$loc", SqliteType.Text);
        var time = command.Parameters.Add("$time", SqliteType.Text);
        var sourceKind = command.Parameters.Add("$sourceKind", SqliteType.Text);
        var kind = command.Parameters.Add("$kind", SqliteType.Text);
        var payment = command.Parameters.Add("$payment", SqliteType.Text);
        var amount = command.Parameters.Add("$amount", SqliteType.Integer);
        var document = command.Parameters.Add("$document", SqliteType.Text);
        var fn = command.Parameters.Add("$fn", SqliteType.Text);
        var serial = command.Parameters.Add("$serial", SqliteType.Text);
        var rnm = command.Parameters.Add("$rnm", SqliteType.Text);
        var name = command.Parameters.Add("$name", SqliteType.Text);
        var shift = command.Parameters.Add("$shift", SqliteType.Integer);

        var inserted = 0;
        var updated = 0;
        var seenInBatch = new HashSet<string>(StringComparer.Ordinal);

        foreach (var operation in operations)
        {
            var key = operation.Source + "\n" + operation.ExternalId;
            var alreadySeen = !seenInBatch.Add(key);
            var existed = alreadySeen;
            if (!alreadySeen)
            {
                existsSource.Value = operation.Source;
                existsExternal.Value = operation.ExternalId;
                existed = Convert.ToInt64(exists.ExecuteScalar()) != 0;
            }

            id.Value = Guid.NewGuid().ToString();
            source.Value = operation.Source;
            external.Value = operation.ExternalId;
            organization.Value = operation.OrganizationId.ToString();
            location.Value = (object?)operation.LocationId?.ToString() ?? DBNull.Value;
            time.Value = operation.OccurredAt.ToString("O");
            sourceKind.Value = operation.SourceKind.ToString();
            kind.Value = operation.Kind.ToString();
            payment.Value = operation.Payment.ToString();
            amount.Value = Money.ToKopecks(operation.Amount);
            document.Value = (object?)operation.SourceDocumentId ?? DBNull.Value;
            fn.Value = operation.FiscalDriveNumber ?? string.Empty;
            serial.Value = operation.KktSerial ?? string.Empty;
            rnm.Value = operation.RegistrationNumber ?? string.Empty;
            name.Value = operation.RegisterDisplayName ?? string.Empty;
            shift.Value = (object?)operation.ShiftNumber ?? DBNull.Value;
            command.ExecuteNonQuery();

            if (existed) updated++;
            else inserted++;
        }

        transaction.Commit();
        return new FiscalBatchResult(inserted, updated);
    }

    private static SqliteConnection Open(Database database)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString());
        db.Open();
        return db;
    }
}
