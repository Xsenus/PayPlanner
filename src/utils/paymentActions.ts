import type { Payment, PaymentPayload } from '../types';

function optionalNumber(value: number | null | undefined): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function optionalString(value: string | null | undefined): string | undefined {
  const trimmed = value?.trim();
  return trimmed ? trimmed : undefined;
}

export function buildCancelledPaymentPayload(payment: Payment): PaymentPayload {
  const baseDate = payment.date || payment.plannedDate;

  return {
    date: baseDate,
    plannedDate: payment.plannedDate || baseDate,
    amount: Math.max(payment.amount ?? 0, 0),
    paidAmount: 0,
    lastPaymentDate: undefined,
    type: payment.type,
    status: 'Cancelled',
    description: payment.description ?? '',
    isPaid: false,
    paidDate: undefined,
    notes: payment.notes ?? '',
    systemNotes: payment.systemNotes ?? '',
    rescheduleCount: payment.rescheduleCount ?? 0,
    clientId: optionalNumber(payment.clientId),
    expenseClientId: payment.type === 'Expense' ? optionalNumber(payment.expenseClientId) : undefined,
    clientCaseId: optionalNumber(payment.clientCaseId),
    contractId: optionalNumber(payment.contractId),
    dealTypeId: optionalNumber(payment.dealTypeId),
    incomeTypeId: optionalNumber(payment.incomeTypeId),
    paymentSourceId: optionalNumber(payment.paymentSourceId),
    paymentStatusId: optionalNumber(payment.paymentStatusId),
    paymentOrderNumber: optionalString(payment.paymentOrderNumber),
    paymentDocumentDate: payment.paymentDocumentDate ?? undefined,
    invoiceId: optionalNumber(payment.invoiceId),
    account: optionalString(payment.account),
    accountDate: payment.accountDate ?? undefined,
  };
}
