using KopCashDesk.Core;
using KopCashDesk.Data;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class TerminalBindingEditTests
{
    [Fact]
    public void EditableTerminal_CanChangeTidAndMoveToAnotherPoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "KopCashDesk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = new Database(Path.Combine(root, "cashdesk.db"));
        db.Initialize();

        var organization = new Organization(Guid.NewGuid(), "ООО Гарант", "6600000000");
        var firstPoint = new Location(Guid.NewGuid(), organization.Id, "Пышма Верхняя", "Адрес 1");
        var secondPoint = new Location(Guid.NewGuid(), organization.Id, "ЦХиЭГ", "Адрес 2");
        db.Save(organization);
        db.Save(firstPoint);
        db.Save(secondPoint);

        var terminal = new TerminalBinding(Guid.NewGuid(), organization.Id, firstPoint.Id, "Sber", "11111111", "MID-1", "POS");
        db.SaveEditableTerminal(terminal);

        var edited = terminal with { LocationId = secondPoint.Id, TerminalId = "22222222", MerchantId = "MID-2", PaymentMethod = "SBP" };
        db.SaveEditableTerminal(edited);

        var actual = Assert.Single(db.TerminalBindings());
        Assert.Equal(terminal.Id, actual.Id);
        Assert.Equal(secondPoint.Id, actual.LocationId);
        Assert.Equal("22222222", actual.TerminalId);
        Assert.Equal("MID-2", actual.MerchantId);
        Assert.Equal("SBP", actual.PaymentMethod);
    }

    [Fact]
    public void EditableTerminal_RejectsDuplicateTidForSameProviderAndOrganization()
    {
        var root = Path.Combine(Path.GetTempPath(), "KopCashDesk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = new Database(Path.Combine(root, "cashdesk.db"));
        db.Initialize();

        var organization = new Organization(Guid.NewGuid(), "ООО Гарант", "6600000000");
        var point = new Location(Guid.NewGuid(), organization.Id, "Кафе", "Адрес");
        db.Save(organization);
        db.Save(point);

        db.SaveEditableTerminal(new TerminalBinding(Guid.NewGuid(), organization.Id, point.Id, "Sber", "11111111", "", "POS"));
        var second = new TerminalBinding(Guid.NewGuid(), organization.Id, point.Id, "Sber", "22222222", "", "QR");
        db.SaveEditableTerminal(second);

        Assert.Throws<InvalidOperationException>(() =>
            db.SaveEditableTerminal(second with { TerminalId = "11111111" }));
    }
}
