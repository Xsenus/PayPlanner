import type { Act, ClientBalanceTransaction, Invoice, Payment } from "../../types";
import { toDateInputValue, toRuDate } from "../../utils/dateUtils";
import { toTimeZoneDateKey } from "../../utils/timeZoneDate";

type ReconciliationSourceKind = "invoice" | "act" | "payment" | "balance";

export type ReconciliationRow = {
  index: number;
  date: string;
  documentNumber: string;
  documentType: string;
  description: string;
  debit: number;
  credit: number;
  balance: number;
  note: string;
  sourceKind: ReconciliationSourceKind;
  sourceId: number;
};

export type BuildReconciliationRowsOptions = {
  invoices: Invoice[];
  payments: Payment[];
  acts: Act[];
  periodFrom: string;
  periodTo: string;
  caseId: string;
  contractId: string;
  includeInvoices: boolean;
  includeActs: boolean;
  includePayments: boolean;
  includeAdvances?: boolean;
  balanceTransactions?: ClientBalanceTransaction[];
  timeZoneId?: string | null;
};

const SOURCE_ORDER: Record<ReconciliationSourceKind, number> = {
  invoice: 1,
  act: 2,
  payment: 3,
  balance: 4,
};

const BALANCE_TRANSACTION_LABELS: Record<ClientBalanceTransaction["type"], string> = {
  Overpayment: "Переплата",
  CreditApplied: "Зачет аванса",
  ManualAdjustment: "Корректировка баланса",
  Refund: "Возврат",
  AdvanceFromCancelledInvoice: "Аванс по аннулированному счету",
  UnallocatedAdvance: "Нераспределенный аванс",
};

const ZONED_DATE_TIME_RE = /^\d{4}-\d{2}-\d{2}T.+(?:Z|[+-]\d{2}:?\d{2})$/i;

function normalizeDate(value: string | Date | null | undefined, timeZoneId?: string | null) {
  if (value instanceof Date) {
    return toTimeZoneDateKey(value, timeZoneId) || toDateInputValue(value);
  }

  if (typeof value === "string" && ZONED_DATE_TIME_RE.test(value)) {
    return toTimeZoneDateKey(value, timeZoneId) || toDateInputValue(value);
  }

  return toDateInputValue(value);
}

function isWithinPeriod(date: string, from: string, to: string) {
  if (!date) return false;
  if (from && date < from) return false;
  if (to && date > to) return false;
  return true;
}

function selectedNumber(value: string) {
  if (value === "all") return null;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

export function roundMoney(value: number) {
  return Math.round((value + Number.EPSILON) * 100) / 100;
}

function compactText(parts: Array<string | null | undefined>) {
  return parts
    .map((part) => part?.trim())
    .filter((part): part is string => Boolean(part))
    .join(" · ");
}

function getInvoiceCaseIds(invoice: Invoice) {
  const ids = new Set<number>();
  if (invoice.clientCaseId) ids.add(invoice.clientCaseId);
  invoice.clientCaseIds?.forEach((id) => ids.add(id));
  invoice.clientCases?.forEach((item) => ids.add(item.id));
  return ids;
}

function invoiceMatchesCase(invoice: Invoice, caseId: number | null) {
  if (!caseId) return true;
  return getInvoiceCaseIds(invoice).has(caseId);
}

function paymentMatchesCase(
  payment: Payment,
  caseId: number | null,
  invoiceById: Map<number, Invoice>,
) {
  if (!caseId) return true;
  if (payment.clientCaseId === caseId || payment.clientCase?.id === caseId) {
    return true;
  }
  const invoice = payment.invoiceId ? invoiceById.get(payment.invoiceId) : null;
  return invoice ? invoiceMatchesCase(invoice, caseId) : false;
}

function actMatchesCase(
  act: Act,
  caseId: number | null,
  invoiceById: Map<number, Invoice>,
) {
  if (!caseId) return true;
  const invoice = act.invoiceId ? invoiceById.get(act.invoiceId) : null;
  return invoice ? invoiceMatchesCase(invoice, caseId) : false;
}

function matchesContract(contractId: number | null | undefined, selectedId: number | null) {
  return !selectedId || contractId === selectedId;
}

function isCancelledWorkflow(status: string | null | undefined) {
  return status === "Cancelled" || status === "Terminated";
}

function getPaymentActualDate(payment: Payment, timeZoneId?: string | null) {
  return normalizeDate(payment.paidDate ?? payment.lastPaymentDate ?? payment.date, timeZoneId);
}

function getPaymentActualAmount(payment: Payment) {
  const paidAmount = Number(payment.paidAmount ?? 0);
  return paidAmount > 0 ? paidAmount : Number(payment.amount ?? 0);
}

function isActualPayment(payment: Payment) {
  return (
    payment.isPaid ||
    payment.status === "Completed" ||
    Number(payment.paidAmount ?? 0) > 0
  );
}

function isPaymentBackedAdvanceTransaction(transaction: ClientBalanceTransaction) {
  return (
    transaction.type === "UnallocatedAdvance" ||
    transaction.type === "AdvanceFromCancelledInvoice"
  );
}

function isAdvanceReviewPayment(payment: Payment) {
  return (
    payment.advanceReviewStatus === "AcceptedAsAdvance" ||
    payment.advanceReviewStatus === "RequiresDecision"
  );
}

function createInvoiceRow(invoice: Invoice, timeZoneId?: string | null): Omit<ReconciliationRow, "index" | "balance"> {
  const amount = Number(invoice.amount ?? 0);
  const isExpense = invoice.type === "Expense";
  const date = normalizeDate(invoice.date || invoice.createdAt, timeZoneId);

  return {
    date,
    documentNumber: invoice.number ? `№${invoice.number}` : `ID ${invoice.id}`,
    documentType: "Счёт",
    description: compactText([
      `Счёт ${invoice.number ? `№${invoice.number}` : `ID ${invoice.id}`}`,
      toRuDate(date),
      invoice.description,
    ]),
    debit: isExpense ? 0 : amount,
    credit: isExpense ? amount : 0,
    note: invoice.workflowStatus ? `Статус документа: ${invoice.workflowStatus}` : "",
    sourceKind: "invoice",
    sourceId: invoice.id,
  };
}

function createActRow(act: Act, timeZoneId?: string | null): Omit<ReconciliationRow, "index" | "balance"> {
  const amount = Number(act.amount ?? 0);
  const isExpense = act.type === "Expense";
  const date = normalizeDate(act.date || act.createdAt, timeZoneId);

  return {
    date,
    documentNumber: act.number ? `№${act.number}` : `ID ${act.id}`,
    documentType: "Акт",
    description: compactText([
      `Акт ${act.number ? `№${act.number}` : `ID ${act.id}`}`,
      toRuDate(date),
      act.title,
      act.comment,
    ]),
    debit: isExpense ? 0 : amount,
    credit: isExpense ? amount : 0,
    note: act.workflowStatus ? `Статус документа: ${act.workflowStatus}` : "",
    sourceKind: "act",
    sourceId: act.id,
  };
}

function createPaymentRow(payment: Payment, timeZoneId?: string | null): Omit<ReconciliationRow, "index" | "balance"> {
  const amount = getPaymentActualAmount(payment);
  const isExpense = payment.type === "Expense";
  const date = getPaymentActualDate(payment, timeZoneId);
  const isAcceptedAdvance = payment.advanceReviewStatus === "AcceptedAsAdvance";
  const needsAdvanceDecision = payment.advanceReviewStatus === "RequiresDecision";
  const documentNumber = compactText([
    payment.paymentOrderNumber ? `№${payment.paymentOrderNumber}` : null,
    payment.id ? `ID ${payment.id}` : null,
  ]);

  return {
    date,
    documentNumber: documentNumber || `ID ${payment.id}`,
    documentType: isAcceptedAdvance ? "Аванс" : "Платёж",
    description: compactText([
      isAcceptedAdvance ? "Нераспределенный аванс" : null,
      needsAdvanceDecision ? "Платеж требует разбора аванса" : null,
      payment.description,
      payment.paymentSource?.name,
      payment.account ? `Счёт ${payment.account}` : null,
    ]),
    debit: isExpense ? amount : 0,
    credit: isExpense ? 0 : amount,
    note: payment.paymentDocumentDate
      ? `Дата документа: ${toRuDate(normalizeDate(payment.paymentDocumentDate, timeZoneId))}`
      : "",
    sourceKind: "payment",
    sourceId: payment.id,
  };
}

function createBalanceTransactionRow(
  transaction: ClientBalanceTransaction,
  timeZoneId?: string | null,
): Omit<ReconciliationRow, "index" | "balance"> {
  const amount = Number(transaction.amount ?? 0);
  const date = normalizeDate(transaction.createdAt, timeZoneId);
  const label = BALANCE_TRANSACTION_LABELS[transaction.type] ?? "Операция баланса";

  return {
    date,
    documentNumber: `ID ${transaction.id}`,
    documentType: label,
    description: compactText([
      label,
      transaction.description,
      transaction.paymentId ? `Платеж ID ${transaction.paymentId}` : null,
      transaction.invoiceId ? `Счет ID ${transaction.invoiceId}` : null,
    ]),
    debit: transaction.direction === "Debit" ? amount : 0,
    credit: transaction.direction === "Credit" ? amount : 0,
    note: transaction.createdByUserName ? `Ответственный: ${transaction.createdByUserName}` : "",
    sourceKind: "balance",
    sourceId: transaction.id,
  };
}

export function buildReconciliationRows({
  invoices,
  payments,
  acts,
  periodFrom,
  periodTo,
  caseId,
  contractId,
  includeInvoices,
  includeActs,
  includePayments,
  includeAdvances = false,
  balanceTransactions = [],
  timeZoneId,
}: BuildReconciliationRowsOptions): ReconciliationRow[] {
  const selectedCaseId = selectedNumber(caseId);
  const selectedContractId = selectedNumber(contractId);
  const invoiceById = new Map(invoices.map((invoice) => [invoice.id, invoice]));
  const paymentById = new Map(payments.map((payment) => [payment.id, payment]));
  const includedPaymentIds = new Set<number>();
  const rows: Array<Omit<ReconciliationRow, "index" | "balance">> = [];

  if (includeInvoices) {
    invoices.forEach((invoice) => {
      if (isCancelledWorkflow(invoice.status) || isCancelledWorkflow(invoice.workflowStatus)) {
        return;
      }
      const date = normalizeDate(invoice.date || invoice.createdAt, timeZoneId);
      if (!isWithinPeriod(date, periodFrom, periodTo)) return;
      if (!invoiceMatchesCase(invoice, selectedCaseId)) return;
      if (!matchesContract(invoice.contractId, selectedContractId)) return;
      rows.push(createInvoiceRow(invoice, timeZoneId));
    });
  }

  if (includeActs) {
    acts.forEach((act) => {
      if (isCancelledWorkflow(act.status) || isCancelledWorkflow(act.workflowStatus)) {
        return;
      }
      const date = normalizeDate(act.date || act.createdAt, timeZoneId);
      if (!isWithinPeriod(date, periodFrom, periodTo)) return;
      if (!actMatchesCase(act, selectedCaseId, invoiceById)) return;
      if (!matchesContract(act.contractId, selectedContractId)) return;
      rows.push(createActRow(act, timeZoneId));
    });
  }

  if (includePayments) {
    payments.forEach((payment) => {
      if (isCancelledWorkflow(payment.status) || !isActualPayment(payment)) {
        return;
      }
      if (!includeAdvances && isAdvanceReviewPayment(payment)) {
        return;
      }
      const date = getPaymentActualDate(payment, timeZoneId);
      if (!isWithinPeriod(date, periodFrom, periodTo)) return;
      if (!paymentMatchesCase(payment, selectedCaseId, invoiceById)) return;
      if (!matchesContract(payment.contractId, selectedContractId)) return;
      includedPaymentIds.add(payment.id);
      rows.push(createPaymentRow(payment, timeZoneId));
    });
  }

  if (includeAdvances) {
    balanceTransactions.forEach((transaction) => {
      const date = normalizeDate(transaction.createdAt, timeZoneId);
      const payment = transaction.paymentId ? paymentById.get(transaction.paymentId) : null;
      const invoice = transaction.invoiceId ? invoiceById.get(transaction.invoiceId) : null;

      if (!isWithinPeriod(date, periodFrom, periodTo)) return;
      if (selectedCaseId) {
        const caseMatched =
          (payment ? paymentMatchesCase(payment, selectedCaseId, invoiceById) : false) ||
          (invoice ? invoiceMatchesCase(invoice, selectedCaseId) : false);
        if (!caseMatched) return;
      }
      if (selectedContractId) {
        const contractMatched =
          matchesContract(payment?.contractId, selectedContractId) ||
          matchesContract(invoice?.contractId, selectedContractId);
        if (!contractMatched) return;
      }
      if (
        transaction.paymentId &&
        includedPaymentIds.has(transaction.paymentId) &&
        isPaymentBackedAdvanceTransaction(transaction)
      ) {
        return;
      }
      rows.push(createBalanceTransactionRow(transaction, timeZoneId));
    });
  }

  let balance = 0;
  return rows
    .sort((left, right) => {
      const dateCompare = left.date.localeCompare(right.date);
      if (dateCompare !== 0) return dateCompare;
      const sourceCompare = SOURCE_ORDER[left.sourceKind] - SOURCE_ORDER[right.sourceKind];
      if (sourceCompare !== 0) return sourceCompare;
      return left.sourceId - right.sourceId;
    })
    .map((row, index) => {
      balance = roundMoney(balance + row.debit - row.credit);
      return {
        ...row,
        index: index + 1,
        balance,
      };
    });
}
