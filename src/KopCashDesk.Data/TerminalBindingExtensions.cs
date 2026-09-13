using KopCashDesk.Core;
using Microsoft.Data.Sqlite;
using System.Globalization;

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
        var old = ReadById(db, transaction, terminal.Id);

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
                INSERT INTO terminal_bindings(
                    id,organization_id,location_id,provider,tid,mid,payment_method,binding_source,is_locked,valid_from,valid_to)
                VALUES($id,$org,$loc,$provider,$tid,$mid,$method,'Manual',1,$from,$to)
                ON CONFLICT(id) DO UPDATE SET
                    organization_id=excluded.organization_id,
                    location_id=excluded.location_id,
                    provider=excluded.provider,
                    tid=excluded.tid,
                    mid=excluded.mid,
                    payment_method=excluded.payment_method,
                    binding_source='Manual',
                    is_locked=1,
                    valid_from=excluded.valid_from,
                    valid_to=excluded.valid_to
                """;
            command.Parameters.AddWithValue("$id", terminal.Id.ToString());
            command.Parameters.AddWithValue("$org", terminal.OrganizationId.ToString());
            command.Parameters.AddWithValue("$loc", terminal.LocationId.ToString());
            command.Parameters.AddWithValue("$provider", terminal.Provider.Trim());
            command.Parameters.AddWithValue("$tid", terminal.TerminalId.Trim());
            command.Parameters.AddWithValue("$mid", terminal.MerchantId.Trim());
            command.Parameters.AddWithValue("$method", string.IsNullOrWhiteSpace(terminal.PaymentMethod) ? "POS" : terminal.PaymentMethod.Trim());
            command.Parameters.AddWithValue("$from", terminal.ValidFrom is null ? DBNull.Value : terminal.ValidFrom.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$to", terminal.ValidTo is null ? DBNull.Value : terminal.ValidTo.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        var newValue = terminal with { BindingSource = BindingSource.Manual, IsLocked = true };
        Audit(db, transaction, "terminal.save", old is null
            ? $"id={newValue.Id}; old=<new>; new={Describe(newValue)}"
            : $"id={newValue.Id}; old={Describe(old)}; new={Describe(newValue)}");
        transaction.Commit();
    }

    public static void DeleteTerminal(this Database database, Guid terminalId)
    {
        using var db = Open(database);
        using var transaction = db.BeginTransaction();
        var old = ReadById(db, transaction, terminalId);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM terminal_bindings WHERE id=$id";
        command.Parameters.AddWithValue("$id", terminalId.ToString());
        var affected = command.ExecuteNonQuery();
        if (affected > 0)
            Audit(db, transaction, "terminal.delete", $"id={terminalId}; old={Describe(old)}");
        transaction.Commit();
    }

    private static TerminalBinding? ReadById(SqliteConnection db, SqliteTransaction tx, Guid id)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT id,organization_id,location_id,provider,tid,mid,payment_method,binding_source,is_locked,valid_from,valid_to
            FROM terminal_bindings WHERE id=$id LIMIT 1
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new(
            Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
            reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
            Enum.TryParse<BindingSource>(reader.GetString(7), true, out var source) ? source : BindingSource.Automatic,
            reader.GetInt64(8) != 0,
            reader.IsDBNull(9) ? null : DateOnly.ParseExact(reader.GetString(9), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            reader.IsDBNull(10) ? null : DateOnly.ParseExact(reader.GetString(10), "yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private static string Describe(TerminalBinding? terminal) => terminal is null
        ? "<none>"
        : $"org={terminal.OrganizationId},loc={terminal.LocationId},provider={terminal.Provider},tid={terminal.TerminalId},mid={terminal.MerchantId},method={terminal.PaymentMethod},source={terminal.BindingSource},locked={terminal.IsLocked},from={terminal.ValidFrom},to={terminal.ValidTo}";

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
