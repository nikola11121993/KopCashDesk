namespace KopCashDesk.Core;

public sealed record Organization(Guid Id, string Name, string TaxId = "");
public sealed record Location(Guid Id, Guid OrganizationId, string Name, string Address = "", bool IsExcluded = false);
public sealed record RegisterBinding(Guid Id, Guid OrganizationId, Guid LocationId, string FiscalDriveNumber, string RegisterNumber = "");
public sealed record TerminalBinding(Guid Id, Guid OrganizationId, Guid LocationId, string Provider, string TerminalId, string MerchantId = "", string PaymentMethod = "POS");

public enum SourceKind { Fiscal, Bank, Manual }
public enum OperationKind { Sale, Return, Correction, Unknown }
public enum PaymentKind { Cash, Electronic, Other, Unknown }

public sealed record CashOperation(
    string Source, string ExternalId, Guid OrganizationId, Guid? LocationId,
    DateTimeOffset OccurredAt, SourceKind SourceKind, OperationKind Kind,
    PaymentKind Payment, decimal Amount, string? SourceDocumentId = null);

public sealed record OperationView(
    DateTimeOffset OccurredAt,
    string Organization,
    string Location,
    string Source,
    OperationKind Kind,
    PaymentKind Payment,
    decimal Amount);

public sealed record Reconciliation(decimal? FiscalElectronic, decimal? BankElectronic, decimal? Difference, string Status);

public static class ReconciliationRules
{
    public static Reconciliation Calculate(IEnumerable<CashOperation> operations, bool fiscalComplete, bool bankComplete)
    {
        var rows = operations.ToArray();
        if (rows.Any(x => x.Kind == OperationKind.Unknown || x.Payment == PaymentKind.Unknown))
            return new(null, null, null, "Требуется разбор операций");
        var fiscal = rows.Where(x => x.SourceKind == SourceKind.Fiscal && x.Payment == PaymentKind.Electronic).Sum(x => x.Amount);
        var bank = rows.Where(x => x.SourceKind == SourceKind.Bank && x.Payment == PaymentKind.Electronic).Sum(x => x.Amount);
        if (!fiscalComplete || !bankComplete)
            return new(fiscalComplete ? fiscal : null, bankComplete ? bank : null, null, "Неполные данные");
        var difference = fiscal - bank;
        return new(fiscal, bank, difference, difference == 0 ? "Сошлось" : "Расхождение");
    }
}

public enum IntegrationKind { Taxcom, AstralOfd, Email }
public sealed record IntegrationProfile(Guid Id, Guid OrganizationId, IntegrationKind Kind, string Name, string SettingsJson, bool Enabled);

public interface IIntegrationAdapter
{
    IntegrationKind Kind { get; }
    Task<IntegrationCheck> CheckAsync(IntegrationProfile profile, CancellationToken cancellationToken);
}
public sealed record IntegrationCheck(bool Success, string Message);

public static class Money
{
    public static decimal Normalize(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    public static long ToKopecks(decimal value) => checked((long)(Normalize(value) * 100m));
    public static decimal FromKopecks(long value) => value / 100m;
}
