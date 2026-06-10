# Delete behavior contract

Inventory date: 2026-06-10.

This document records the current EF delete behavior for the core business entities named in the roadmap. It is guarded by `DeleteBehaviorContractTests` for SQLite and PostgreSQL model metadata.

| Deleted entity | Cascades | Set null / preserved references | Restrict / caveats |
| --- | --- | --- | --- |
| Client | Client cases and client-contract join rows are cascaded. | Payments keep their rows and clear `ClientId` / `ExpenseClientId`. | Client delete is high impact because cases cascade from the client. |
| Client case | Case documents and invoice-case join rows are cascaded. | Payments keep their rows and clear `ClientCaseId`. | Case-owned legal/court/deadline rows have additional cascade/set-null behavior outside the minimum roadmap scope. |
| Payment | Payment responsibles, invoice line items, contract-payment join rows, and invoice-case join rows are cascaded. | Cash orders and client balance transactions keep their rows and clear `PaymentId`. | Remainder transfer links use restrict behavior in the model and are intentionally not auto-deleted. |
| Invoice | No primary invoice-owned aggregate is deleted through invoice deletion in the minimum contract. | Payments, cash orders, and client balance transactions keep rows and clear `InvoiceId`. | Invoice line items belong to `Payment` in the current model, so payment deletion controls those rows. Act invoice linkage is legacy nullable data and is not enforced as an `Invoice` FK in this contract. |
| Act | Act line items cascade. | Contract-act join rows cascade through the contract/act join model. | Linked invoice reference on act is nullable and set-null when invoice is deleted. |
| Contract | Client/legal entity join rows and contract-act/invoice/payment join rows are cascaded. | Payments keep rows and clear `ContractId`. | Contract deletion does not delete payment rows. |
| Documents / attachments | Task journal files cascade with their task. Case documents cascade with their case. | Procedural deadlines and court events keep rows and clear `ClientCaseDocumentId` when a linked case document is deleted. | Physical file cleanup is controller/storage behavior and is separate from EF delete behavior. |

## Follow-up notes

- Changing client-to-case cascade would be a business-rule and migration-sensitive change because it affects legal case data retention.
- Physical attachment cleanup must be tested separately from EF relationship metadata.
- Delete behavior here describes database relationship behavior; soft-delete audit hooks can still change what user actions do before a hard delete reaches the database.
