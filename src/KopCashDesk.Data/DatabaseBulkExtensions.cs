using KopCashDesk.Core;
using Microsoft.Data.Sqlite;

namespace KopCashDesk.Data;

public static class DatabaseBulkExtensions
{
    public static int InsertOperationsBatch(this Database database, IReadOnlyCollection<CashOperation> operations)
    {
        if (operations.Count == 0) return 0;

        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString());
        db.Open();
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
}
