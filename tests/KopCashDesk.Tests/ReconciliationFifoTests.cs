using KopCashDesk.Core;
using KopCashDesk.Data;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class ReconciliationFifoTests
{
    [Fact]
    public void LaterCash_ClosesOldestTerminalDays_FIFO()
    {
        using var fixture = new Fixture();
        fixture.Bank(2026, 9, 10, 15000m, "b10");
        fixture.Bank(2026, 9, 11, 20000m, "b11");
        fixture.Bank(2026, 9, 12, 10000m, "b12");
        fixture.Cash(2026, 9, 15, 45000m, "c15");

        var rows = fixture.Days().ToDictionary(x => x.Date);

        Assert.Equal(0m, rows[new DateOnly(2026, 9, 10)].DayRemaining);
        Assert.Equal(15000m, rows[new DateOnly(2026, 9, 10)].ClosedLater);
        Assert.Equal(new DateOnly(2026, 9, 15), rows[new DateOnly(2026, 9, 10)].LastSettlementDate);
        Assert.Equal(20000m, rows[new DateOnly(2026, 9, 11)].ClosedLater);
        Assert.Equal(10000m, rows[new DateOnly(2026, 9, 12)].ClosedLater);
        Assert.Equal(45000m, rows[new DateOnly(2026, 9, 15)].PriorOutstanding);
        Assert.Equal(45000m, rows[new DateOnly(2026, 9, 15)].CashAppliedOnDate);
        Assert.Equal(0m, rows[new DateOnly(2026, 9, 15)].CumulativeOutstanding);
        Assert.All(rows.Values.Where(x => x.HasBankData), x => Assert.Equal("Пробито позже", x.Status));
        Assert.Equal(3, fixture.Allocations().Count(x => x.Kind == ReconciliationAllocationKind.Cash));
    }

    [Fact]
    public void PartialCash_LeavesNextDayRemainder()
    {
        using var fixture = new Fixture();
        fixture.Bank(2026, 9, 10, 15000m, "b10");
        fixture.Bank(2026, 9, 11, 20000m, "b11");
        fixture.Cash(2026, 9, 15, 18000m, "c15");

        var rows = fixture.Days().ToDictionary(x => x.Date);

        Assert.Equal(0m, rows[new DateOnly(2026, 9, 10)].DayRemaining);
        Assert.Equal(15000m, rows[new DateOnly(2026, 9, 10)].ClosedLater);
        Assert.Equal(17000m, rows[new DateOnly(2026, 9, 11)].DayRemaining);
        Assert.Equal(3000m, rows[new DateOnly(2026, 9, 11)].ClosedLater);
        Assert.Equal("Частично пробито", rows[new DateOnly(2026, 9, 11)].Status);
        Assert.Equal(17000m, rows[new DateOnly(2026, 9, 15)].CumulativeOutstanding);
    }

    [Fact]
    public void OneTerminalDay_CanBeClosedBySeveralCashAmounts()
    {
        using var fixture = new Fixture();
        fixture.Bank(2026, 9, 10, 35000m, "b10");
        fixture.Cash(2026, 9, 12, 10000m, "c12");
        fixture.Cash(2026, 9, 13, 25000m, "c13");

        var row = Assert.Single(fixture.Days().Where(x => x.Date == new DateOnly(2026, 9, 10)));
        Assert.Equal(35000m, row.ClosedLater);
        Assert.Equal(0m, row.DayRemaining);
        Assert.Equal(2, fixture.Allocations().Count(x => x.TerminalDate == row.Date && x.Kind == ReconciliationAllocationKind.Cash));
    }

    [Fact]
    public void CashCanCloseTerminalRevenueInNextMonth()
    {
        using var fixture = new Fixture();
        fixture.Bank(2026, 8, 31, 12000m, "b-aug");
        fixture.Cash(2026, 9, 2, 12000m, "c-sep");

        var august = fixture.Database.ReconciliationDays(fixture.Organization.Id, 2026, 8, fixture.Location.Id);
        var row = Assert.Single(august);
        Assert.Equal("Пробито позже", row.Status);
        Assert.Equal(new DateOnly(2026, 9, 2), row.LastSettlementDate);
        Assert.Equal(0m, row.DayRemaining);
    }

    [Fact]
    public void ImportOrder_DoesNotChangeResult()
    {
        using var first = new Fixture();
        first.Bank(2026, 9, 10, 15000m, "b10");
        first.Bank(2026, 9, 11, 20000m, "b11");
        first.Cash(2026, 9, 15, 35000m, "c15");
        var expected = Snapshot(first.Days());

        using var second = new Fixture();
        second.Cash(2026, 9, 15, 35000m, "c15");
        second.Bank(2026, 9, 11, 20000m, "b11");
        second.Bank(2026, 9, 10, 15000m, "b10");
        var actual = Snapshot(second.Days());

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MissingCash_IsNotZeroOrMatched()
    {
        using var fixture = new Fixture();
        fixture.Bank(2026, 9, 1, 100m, "b1");
        fixture.Bank(2026, 9, 2, 100m, "b2");
        fixture.Cash(2026, 9, 1, 200m, "c1");

        var rows = fixture.Days().ToDictionary(x => x.Date);
        Assert.Null(rows[new DateOnly(2026, 9, 2)].CashElectronic);
        Assert.Equal(100m, rows[new DateOnly(2026, 9, 2)].DayRemaining);
        Assert.Equal("Нет данных кассы", rows[new DateOnly(2026, 9, 2)].Status);
    }

    [Fact]
    public void ManualCashOnlyDate_AppearsInSummaryAndFifo()
    {
        using var fixture = new Fixture();
        fixture.Bank(2026, 9, 10, 45000m, "b10");
        fixture.Database.SetManualCash(fixture.Organization.Id, fixture.Location.Id, new DateOnly(2026, 9, 15), 45000m);

        var summary = fixture.Database.PointDaySummaries(fixture.Organization.Id, 2026, 9, fixture.Location.Id);
        Assert.Contains(summary, x => x.Date == new DateOnly(2026, 9, 15));

        var rows = fixture.Days().ToDictionary(x => x.Date);
        Assert.Equal(45000m, rows[new DateOnly(2026, 9, 15)].CashElectronic);
        Assert.Equal(0m, rows[new DateOnly(2026, 9, 15)].CumulativeOutstanding);
    }

    [Fact]
    public void ExcludedPoint_DoesNotProduceAutomaticAllocations()
    {
        using var fixture = new Fixture(excluded: true);
        fixture.Bank(2026, 9, 10, 1000m, "b10");
        fixture.Cash(2026, 9, 11, 1000m, "c11");

        var rows = fixture.Days();
        Assert.All(rows, x => Assert.True(x.IsExcluded));
        Assert.All(rows, x => Assert.Equal("Исключено из автосверки", x.Status));
        Assert.Empty(fixture.Allocations());
    }

    [Fact]
    public void BankReturns_ReduceOutstandingButAreMarkedForReview()
    {
        using var fixture = new Fixture();
        fixture.Bank(2026, 9, 10, 100m, "sale");
        fixture.Bank(2026, 9, 11, -30m, "refund", OperationKind.Return);
        fixture.Cash(2026, 9, 12, 70m, "cash");

        var rows = fixture.Days().ToDictionary(x => x.Date);
        Assert.Equal(0m, rows[new DateOnly(2026, 9, 12)].CumulativeOutstanding);
        Assert.True(rows[new DateOnly(2026, 9, 11)].RequiresReview);
        Assert.Contains(fixture.Allocations(), x => x.Kind == ReconciliationAllocationKind.BankReturnCredit && x.Amount == 30m);
    }

    [Fact]
    public void ReturnLargerThanOpenSales_CarriesCreditAndRequiresReview()
    {
        using var fixture = new Fixture();
        fixture.Bank(2026, 9, 10, 100m, "sale");
        fixture.Bank(2026, 9, 11, -150m, "refund", OperationKind.Return);
        fixture.Bank(2026, 9, 12, 40m, "later-sale");

        var rows = fixture.Days().ToDictionary(x => x.Date);
        Assert.Equal(0m, rows[new DateOnly(2026, 9, 12)].CumulativeOutstanding);
        Assert.True(rows[new DateOnly(2026, 9, 11)].RequiresReview);
        Assert.True(rows[new DateOnly(2026, 9, 12)].RequiresReview);
    }

    [Fact]
    public void ManualTerminalBinding_IsNotOverwrittenByAutomaticSave()
    {
        using var fixture = new Fixture();
        var other = new Location(Guid.NewGuid(), fixture.Organization.Id, "Другая точка", "Другой адрес");
        fixture.Database.Save(other);

        var terminal = new TerminalBinding(Guid.NewGuid(), fixture.Organization.Id, fixture.Location.Id, "Sber", "111", "222", "POS");
        fixture.Database.SaveEditableTerminal(terminal);
        fixture.Database.Save(new TerminalBinding(Guid.NewGuid(), fixture.Organization.Id, other.Id, "Sber", "111", "999", "QR"));

        var stored = Assert.Single(fixture.Database.TerminalBindings());
        Assert.Equal(fixture.Location.Id, stored.LocationId);
        Assert.Equal("222", stored.MerchantId);
        Assert.Equal(BindingSource.Manual, stored.BindingSource);
        Assert.True(stored.IsLocked);
    }

    private static string[] Snapshot(IEnumerable<ReconciliationDay> rows) => rows
        .OrderBy(x => x.Date)
        .Select(x => $"{x.Date:yyyy-MM-dd}|{x.BankElectronic}|{x.CashElectronic}|{x.ClosedLater}|{x.DayRemaining}|{x.CumulativeOutstanding}|{x.Status}")
        .ToArray();

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder;
        public Database Database { get; }
        public Organization Organization { get; }
        public Location Location { get; }

        public Fixture(bool excluded = false)
        {
            _folder = Path.Combine(Path.GetTempPath(), "KopCashDesk.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            Database = new Database(Path.Combine(_folder, "cashdesk.db"));
            Database.Initialize();
            Organization = new Organization(Guid.NewGuid(), "ООО Тест", "6600000000");
            Location = new Location(Guid.NewGuid(), Organization.Id, "Точка", "Адрес", excluded);
            Database.Save(Organization);
            Database.Save(Location);
        }

        public void Bank(int year, int month, int day, decimal amount, string externalId, OperationKind kind = OperationKind.Sale)
        {
            Database.Insert(new CashOperation(
                "Sber.Acquiring", externalId, Organization.Id, Location.Id,
                At(year, month, day, 12), SourceKind.Bank, kind, PaymentKind.Electronic, amount));
        }

        public void Cash(int year, int month, int day, decimal amount, string externalId)
        {
            Database.UpsertFiscalOperation(new CashOperation(
                "Test.Fiscal", externalId, Organization.Id, Location.Id,
                At(year, month, day, 18), SourceKind.Fiscal,
                amount < 0 ? OperationKind.Return : OperationKind.Sale,
                PaymentKind.Electronic, amount));
        }

        public IReadOnlyList<ReconciliationDay> Days() =>
            Database.ReconciliationDays(Organization.Id, null, null, Location.Id);

        public IReadOnlyList<ReconciliationAllocation> Allocations() =>
            Database.ReconciliationAllocations(Organization.Id, Location.Id);

        private static DateTimeOffset At(int year, int month, int day, int hour) =>
            new(year, month, day, hour, 0, 0, TimeSpan.FromHours(5));

        public void Dispose()
        {
            try { Directory.Delete(_folder, true); }
            catch { }
        }
    }
}
