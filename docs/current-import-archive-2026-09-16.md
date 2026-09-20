# Current production import archive — 2026-09-16

The clean import flow is expected to accept the report bundle supplied on 2026-09-16 without `Unknown` rows.

Supported layouts in that bundle:

- Sber full common acquiring XLSX.
- Sber short settlement XLSX (without INN/TST name columns; INN is taken from the filename and known TID binding is reused).
- Sber reimbursement XLSX.
- Regular Sber acquiring ZIP, including a ZIP that also contains an unrelated Taxcom workbook.
- Taxcom shift summary XLSX.
- Taxcom fiscal-document summary XLSX.
- Alternate `Закрытые смены` XLSX with `Номер смены / Дата закрытия смены / Получено наличными / Получено безналичными` headers.
- UBRiR `Свод по дням (по опердню)` XLSX for terminal `26204835`.

Bank deduplication in the smart Sber importer uses organization + TID + RRN + business date + signed amount + operation kind, so the same transaction present in full, short and reimbursement reports is counted once.

Known physical terminal groups are resolved before KKT import, including ATI:

- `42526205`, `42526204` -> `ЗАВОД АТИ`
- `34723825`, `34723835`, `34723837` -> `Столовая АТИ`

and known replacement/payment-method TIDs for Reftinskaya GRES cafeteria 6, Ladyzhenskogo 7, Chapaeva 28, Mira 4 and other current locations.
