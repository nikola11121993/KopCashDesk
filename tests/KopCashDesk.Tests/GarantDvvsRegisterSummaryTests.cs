using KopCashDesk.Core;
using KopCashDesk.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KopCashDesk.Tests;

public sealed class GarantDvvsRegisterSummaryTests
{
    [Fact]
    public void DailyDvvsSummary_GroupsByPhysicalKkt_AndIgnoresFnReplacement()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dvvs-summary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var databasePath = Path.Combine(folder, "cashdesk.db");

        try
        {
            var db = new Database(databasePath);
            db.Initialize();

            var organization = new Organization(Guid.NewGuid(), "ООО ГАРАНТ", KnownBusinessRules.GarantTaxId);
            var point = new Location(Guid.NewGuid(), organization.Id, KnownBusinessRules.GarantDvvsPointName, "Екатеринбург, Универсиады, 11");
            db.Save(organization);
            db.Save(point);
            KnownBusinessRules.ApplyPending(db);

            var day = new DateTimeOffset(2026, 4, 4, 12, 0, 0, TimeSpan.FromHours(5));
            SaveFiscal(db, organization.Id, point.Id, "k1-old", day, 4390m, KnownBusinessRules.GarantDvvs1Serial, KnownBusinessRules.GarantDvvs1Rnm, "7384440900680170", "ДВВС Касса 1");
            SaveFiscal(db, organization.Id, point.Id, "k1-new", day.AddMinutes(1), 610m, KnownBusinessRules.GarantDvvs1Serial, KnownBusinessRules.GarantDvvs1Rnm, "7384441001761900", "ДВВС Касса 1");
            SaveFiscal(db, organization.Id, point.Id, "k2", day.AddMinutes(2), 2500m, KnownBusinessRules.GarantDvvs2Serial, KnownBusinessRules.GarantDvvs2Rnm, "7380440902207031", "ДВВС Касса 2");
            SaveFiscal(db, organization.Id, point.Id, "spare", day.AddMinutes(3), 300m, KnownBusinessRules.GarantDvvsSpareSerial, KnownBusinessRules.GarantDvvsSpareRnm, "7380440801563017", "Запасная ДВВС");

            // Other fiscal projections and bank rows must not be added to this dedicated Taxcom-documents view.
            db.UpsertFiscalOperation(new(
                "Taxcom.ShiftReport", "shift-derived", organization.Id, point.Id, day.AddMinutes(4),
                SourceKind.Fiscal, OperationKind.Sale, PaymentKind.Electronic, 999m,
                FiscalDriveNumber: "7384441001761900", KktSerial: KnownBusinessRules.GarantDvvs1Serial,
                RegistrationNumber: KnownBusinessRules.GarantDvvs1Rnm, RegisterDisplayName: "ДВВС Касса 1"));
            db.Insert(new(
                "Sber", "bank", organization.Id, point.Id, day.AddMinutes(5),
                SourceKind.Bank, OperationKind.Sale, PaymentKind.Electronic, 777m));

            var row = Assert.Single(db.GarantDvvsRegisterDaySummaries(2026, 4, organization.Id, point.Id));
            Assert.Equal(new DateOnly(2026, 4, 4), row.Date);
            Assert.Equal(5000m, row.Kassa1Electronic);
            Assert.Equal(2500m, row.Kassa2Electronic);
            Assert.Equal(300m, row.SpareElectronic);
            Assert.Equal(7800m, row.TotalElectronic);

            var registers = db.GarantDvvsRegisterRevenueSummaries(2026, 4, organization.Id, point.Id);
            Assert.Equal(3, registers.Count);
            Assert.Equal(5000m, Assert.Single(registers, x => x.KktSerial == KnownBusinessRules.GarantDvvs1Serial).Electronic);
            Assert.Equal(2500m, Assert.Single(registers, x => x.KktSerial == KnownBusinessRules.GarantDvvs2Serial).Electronic);
            Assert.Equal(300m, Assert.Single(registers, x => x.KktSerial == KnownBusinessRules.GarantDvvsSpareSerial).Electronic);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    private static void SaveFiscal(
        Database db,
        Guid organizationId,
        Guid locationId,
        string externalId,
        DateTimeOffset occurredAt,
        decimal amount,
        string serial,
        string rnm,
        string fn,
        string name)
    {
        db.UpsertFiscalOperation(new(
            "Taxcom.FiscalDocuments",
            externalId,
            organizationId,
            locationId,
            occurredAt,
            SourceKind.Fiscal,
            OperationKind.Sale,
            PaymentKind.Electronic,
            amount,
            FiscalDriveNumber: fn,
            KktSerial: serial,
            RegistrationNumber: rnm,
            RegisterDisplayName: name,
            ShiftNumber: 1));
    }
}
