import type { Invoice, Payment } from '../types';
import { toDateInputValue } from './dateUtils';

export const INSTALLMENT_PAYMENT_MARKER = '[pp:installment-payment]';

export function normalizeInvoiceNumberKey(value?: string | null): string | null {
  const normalized = String(value ?? '').trim().toLowerCase();
  if (!normalized) return null;

  const numeric = normalized.replace(/\D+/g, '');
  if (numeric) return numeric;

  const stripped = normalized.replace(/[^a-z0-9а-яё]+/gi, '');
  return stripped || normalized;
}

export function getInvoiceEntityId(value?: Invoice | null): number | null {
  return typeof value?.invoiceEntityId === 'number' && value.invoiceEntityId > 0
    ? value.invoiceEntityId
    : null;
}

export function getInvoiceLegacyKey(number?: string | null, date?: string | null): string | null {
  const normalizedNumber = normalizeInvoiceNumberKey(number);
  if (!normalizedNumber) return null;

  const normalizedDate = toDateInputValue(date ?? null);
  const year = normalizedDate?.slice(0, 4) ?? 'na';
  return `${normalizedNumber}::${year}`;
}

export function getPaymentLegacyKey(payment: Pick<Payment, 'account' | 'accountDate' | 'date'>): string | null {
  return getInvoiceLegacyKey(payment.account, payment.accountDate ?? payment.date);
}

export function isPaymentLinkedToInvoice(
  payment: Payment,
  invoice: Invoice,
  options?: { includeCurrentInvoiceRecord?: boolean },
): boolean {
  const includeCurrentInvoiceRecord = options?.includeCurrentInvoiceRecord ?? false;
  if (!includeCurrentInvoiceRecord && payment.id === invoice.id) {
    return false;
  }

  const canonicalInvoiceId = getInvoiceEntityId(invoice) ?? (invoice.id > 0 ? invoice.id : null);
  if (canonicalInvoiceId && payment.invoiceId === canonicalInvoiceId) {
    return true;
  }

  const invoiceKey = getInvoiceLegacyKey(invoice.number, invoice.date);
  const paymentKey = getPaymentLegacyKey(payment);
  return Boolean(invoiceKey && paymentKey && invoiceKey === paymentKey);
}

export function filterRelatedPaymentsForInvoice(payments: Payment[], invoice?: Invoice | null): Payment[] {
  return filterRelatedPaymentsForInvoiceWithOptions(payments, invoice);
}

export function filterRelatedPaymentsForInvoiceWithOptions(
  payments: Payment[],
  invoice?: Invoice | null,
  options?: { includeCurrentInvoiceRecord?: boolean },
): Payment[] {
  if (!invoice) {
    return [];
  }

  const uniquePayments = new Map<number, Payment>();
  payments.forEach((payment) => {
    if (isPaymentLinkedToInvoice(payment, invoice, options)) {
      uniquePayments.set(payment.id, payment);
    }
  });

  return Array.from(uniquePayments.values()).sort((left, right) => {
    const leftDate = toDateInputValue(left.plannedDate) || toDateInputValue(left.date) || '';
    const rightDate = toDateInputValue(right.plannedDate) || toDateInputValue(right.date) || '';
    return leftDate.localeCompare(rightDate) || left.id - right.id;
  });
}

export function isGeneratedInstallmentPayment(payment: Pick<Payment, 'systemNotes'>): boolean {
  return Boolean(payment.systemNotes?.includes(INSTALLMENT_PAYMENT_MARKER));
}

export function isInvoiceManagedPayment(
  payment: Pick<Payment, 'documentKind' | 'systemNotes'>,
): boolean {
  return payment.documentKind === 'Invoice' || isGeneratedInstallmentPayment(payment);
}

export function filterRepresentativeInvoiceWhenInstallmentsExist(payments: Payment[]): Payment[] {
  if (!payments.some(isGeneratedInstallmentPayment)) {
    return payments;
  }

  return payments.filter((payment) => payment.documentKind !== 'Invoice');
}
