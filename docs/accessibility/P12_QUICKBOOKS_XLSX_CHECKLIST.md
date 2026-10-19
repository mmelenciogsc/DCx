# P12 QuickBooks XLSX accessibility checklist

Automated SpreadsheetML validation is not a human Microsoft Excel or screen-reader pass. Record a dated tester/result for every manual row before this portion of P12 can be accepted end to end.

| Check | Automated evidence | Human acceptance |
|---|---|---|
| Workbook opens as Office 2007 SpreadsheetML | Microsoft Open XML SDK 3.5.1 reports zero schema errors | BLOCKED — Excel is not installed on the validation host |
| Profit-and-loss rows form one named table with headers and a totals row | XML/table relationship and exact range assertions pass | BLOCKED — verify Excel table navigation and totals announcement |
| Period start/end are typed dates using `yyyy-mm-dd` | Numeric cell/style assertions and 1900 boundary cases pass | BLOCKED — verify locale presentation and spoken values |
| Amounts and total are typed USD currency values | Number-format, numeric-cell and cached-total assertions pass | BLOCKED — verify negative values and currency announcements |
| Only the fixed subtotal is executable | Exact formula allowlist passes; formula-looking hostile label remains inline text | BLOCKED — inspect formula bar and protected-view behavior |
| Reading order and names are understandable | Extracted text contains title, sandbox marker and account labels | BLOCKED — navigate with JAWS, NVDA and Narrator in Excel |
| Existing output survives failure/cancellation | Atomic replacement, invalid input and cancellation tests pass | BLOCKED — manually interrupt a large export and reopen prior workbook |
| Live provider data maps correctly | No QuickBooks request occurs in automated evidence | BLOCKED — requires separately authorized sandbox OAuth/API run |

Automated evidence: `artifacts/test-results/p12-integrations-bc24abeb1de34673842838be84560d1e/` at exact clean source `62914d1`. Real credentials, live provider calls, Excel automation and human assistive-technology claims: zero.
