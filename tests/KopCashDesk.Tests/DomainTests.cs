using KopCashDesk.Core;
using KopCashDesk.Data;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class DomainTests
{
    [Fact]
    public void Reconciliation_UsesOnlyElectronicAmounts()
    {
        var org = Guid.NewGuid();
        var rows = new[]
        {
            new CashOperation("ofd","1",org,null,DateTimeOffset.UtcNow,SourceKind.Fiscal,OperationKind.Sale,PaymentKind.Electronic,120m),
            new CashOperation("ofd","2",org,null,DateTimeOffset.UtcNow,SourceKind.Fiscal,OperationKind.Sale,PaymentKind.Cash,1000m),
            new CashOperation("ofd","3",org,null,DateTimeOffset.UtcNow,SourceKind.Fiscal,OperationKind.Return,PaymentKind.Electronic,-20m),
            new CashOperation("bank","4",org,null,DateTimeOffset.UtcNow,SourceKind.Bank,OperationKind.Sale,PaymentKind.Electronic,105m)
        };
        var result = ReconciliationRules.Calculate(rows, true, true);
        Assert.Equal(100m, result.FiscalElectronic);
        Assert.Equal(105m, result.BankElectronic);
        Assert.Equal(-5m, result.Difference);
        Assert.Equal("Расхождение", result.Status);
    }
    [Fact]
    public void MissingSource_DoesNotPretendToBeZero()
    {
        var result = ReconciliationRules.Calculate([], true, false);
        Assert.Null(result.BankElectronic); Assert.Null(result.Difference); Assert.Equal("Неполные данные", result.Status);
    }
    [Fact]
    public void Money_UsesIntegerKopecks()
    {
        Assert.Equal(12345, Money.ToKopecks(123.45m)); Assert.Equal(123.45m, Money.FromKopecks(12345));
    }
    [Fact]
    public void EmptyDatabase_AndDuplicateProtection()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kop-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var db = new Database(System.IO.Path.Combine(dir,"test.db")); db.Initialize();
            Assert.Empty(db.Organizations()); Assert.Equal(0, db.CountOperations());
            var org = new Organization(Guid.NewGuid(),"Тест"); db.Save(org);
            var op = new CashOperation("test","unique-1",org.Id,null,DateTimeOffset.UtcNow,SourceKind.Fiscal,OperationKind.Sale,PaymentKind.Electronic,123.45m);
            Assert.True(db.Insert(op)); Assert.False(db.Insert(op)); Assert.Equal(1,db.CountOperations());
            var backup = System.IO.Path.Combine(dir,"backup.db"); db.Backup(backup); Assert.True(File.Exists(backup));
            Assert.Single(new Database(backup).Organizations());
        }
        finally { try { Directory.Delete(dir,true); } catch (IOException) { } }
    }
}
