namespace KopCashDesk.Core;

public sealed record PeriodSummary(decimal? Bank, decimal? Cash, bool IsComplete, string Status)
{
    public static PeriodSummary Calculate(IEnumerable<PointDaySummary> source)
    {
        var rows = source.ToArray();
        decimal? Sum(IEnumerable<decimal?> values) => values.Any(x => x.HasValue)
            ? values.Sum(x => x ?? 0m) : null;
        var bank = Sum(rows.Select(x => x.BankElectronic));
        var cash = Sum(rows.Select(x => x.FiscalElectronic));
        var complete = rows.Length > 0 && rows.All(x => !x.HasSourceConflict &&
            x.BankElectronic.HasValue && x.FiscalElectronic.HasValue);
        var status = !complete ? "Неполные данные — требуется проверка"
            : cash == bank ? "Сошлось"
            : $"Расхождение: {cash - bank:N2} ₽";
        return new(bank, cash, complete, status);
    }
}
