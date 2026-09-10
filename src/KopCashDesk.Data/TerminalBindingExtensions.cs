using KopCashDesk.Core;
using Microsoft.Data.Sqlite;

namespace KopCashDesk.Data;

public static class TerminalBindingExtensions
{
    public static void SaveEditableTerminal(this Database database, TerminalBinding terminal)
    {
        if (string.IsNullOrWhiteSpace(terminal.Provider))
            throw new InvalidOperationException("Укажите провайдера терминала.");
        if (string.IsNullOrWhiteSpace(terminal.TerminalId))
            throw new InvalidOperationException("Укажите TID терминала.");

        using var db = Open(database);
        using var transaction = db.BeginTransaction();

        using (var duplicate = db.CreateCommand())
        {
            duplicate.Transaction = transaction;
            duplicate.CommandText = """
                SELECT COUNT(*)
                FROM terminal_bindings
                WHERE organization_id=$org
                  AND provider=$provider
                  AND tid=$tid
                  AND id<>$id
                """;
            duplicate.Parameters.AddWithValue("$org", terminal.OrganizationId.ToString());
            duplicate.Parameters.AddWithValue("$provider", terminal.Provider.Trim());
            duplicate.Parameters.AddWithValue("$tid", terminal.TerminalId.Trim());
            duplicate.Parameters.AddWithValue("$id", terminal.Id.ToString());
            if (Convert.ToInt32(duplicate.ExecuteScalar()) > 0)
                throw new InvalidOperationException("Такой TID уже есть у этой организации и провайдера.");
        }

        using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO terminal_bindings(id,organization_id,location_id,provider,tid,mid,payment_method)
                VALUES($id,$org,$loc,$provider,$tid,$mid,$method)
                ON CONFLICT(id) DO UPDATE SET
                    organization_id=excluded.organization_id,
                    location_id=excluded.location_id,
                    provider=excluded.provider,
                    tid=excluded.tid,
                    mid=excluded.mid,
                    payment_method=excluded.payment_method
                """;
            command.Parameters.AddWithValue("$id", terminal.Id.ToString());
            command.Parameters.AddWithValue("$org", terminal.OrganizationId.ToString());
            command.Parameters.AddWithValue("$loc", terminal.LocationId.ToString());
            command.Parameters.AddWithValue("$provider", terminal.Provider.Trim());
            command.Parameters.AddWithValue("$tid", terminal.TerminalId.Trim());
            command.Parameters.AddWithValue("$mid", terminal.MerchantId.Trim());
            command.Parameters.AddWithValue("$method", string.IsNullOrWhiteSpace(terminal.PaymentMethod) ? "POS" : terminal.PaymentMethod.Trim());
            command.ExecuteNonQuery();
        }

        Audit(db, transaction, "terminal.save",
            $"id={terminal.Id}; org={terminal.OrganizationId}; loc={terminal.LocationId}; provider={terminal.Provider.Trim()}; tid={terminal.TerminalId.Trim()}; mid={terminal.MerchantId.Trim()}; method={terminal.PaymentMethod.Trim()}");
        transaction.Commit();
    }

    public static void DeleteTerminal(this Database database, Guid terminalId)
    {
        using var db = Open(database);
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM terminal_bindings WHERE id=$id";
        command.Parameters.AddWithValue("$id", terminalId.ToString());
        var affected = command.ExecuteNonQuery();
        if (affected > 0)
            Audit(db, transaction, "terminal.delete", $"id={terminalId}");
        transaction.Commit();
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

    private static void Audit(SqliteConnection db, SqliteTransaction transaction, string action, string details)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO audit_log(occurred_at,action,details) VALUES($time,$action,$details)";
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$details", details);
        command.ExecuteNonQuery();
    }
}
