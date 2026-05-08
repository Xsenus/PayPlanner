import type { Invoice, Payment } from '../types';
import { toDateInputValue } from './dateUtils';

export const INSTALLMENT_PAYMENT_MARKER = '[pp:installment-payment]';
export const PARTIAL_REMAINDER_PAYMENT_MARKER = '[pp:partial-remainder]';
export const PARTIAL_REMAINDER_SOURCE_MARKER = '[pp:partial-remainder-source]';

export interface PartialRemainderTransferMetadata {
  sourcePaymentId?: number;
  sourceOriginalAmount?: number;
  paidAmount?: number;
  remainderAmount?: number;
  remainderDate?: string;
}

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

export function isTransferredRemainderPayment(payment: Pick<Payment, 'systemNotes'>): boolean {
  return Boolean(payment.systemNotes?.includes(PARTIAL_REMAINDER_PAYMENT_MARKER));
}

export function isPartialRemainderSourcePayment(payment: Pick<Payment, 'systemNotes'>): boolean {
  return Boolean(payment.systemNotes?.includes(PARTIAL_REMAINDER_SOURCE_MARKER));
}

export function needsPartialRemainderTransfer(
  payment: Pick<Payment, 'amount' | 'paidAmount' | 'status' | 'documentKind' | 'systemNotes'>,
): boolean {
  const amount = Number(payment.amount ?? 0);
  const paidAmount = Number(payment.paidAmount ?? 0);
  return (
    payment.documentKind !== 'Invoice'
    && payment.status !== 'Cancelled'
    && paidAmount > 0
    && amount - paidAmount > 0.009
    && !isPartialRemainderSourcePayment(payment)
  );
}

function readStringProperty(value: unknown, propertyName: string): string | null {
  if (!value || typeof value !== 'object') {
    return null;
  }

  const entry = Object.entries(value as Record<string, unknown>).find(
    ([key]) => key.toLowerCase() === propertyName.toLowerCase(),
  );
  const rawValue = entry?.[1];
  return typeof rawValue === 'string' && rawValue.trim() ? rawValue.trim() : null;
}

function parsePartialRemainderMarkerLine(line: string, marker: string): PartialRemainderTransferMetadata | null {
  const trimmed = line.trim();
  if (!trimmed.startsWith(marker)) {
    return null;
  }

  const rawJson = trimmed.slice(marker.length).trim();
  if (!rawJson.startsWith('{')) {
    return null;
  }

  try {
    const parsed = JSON.parse(rawJson);
    return {
      sourcePaymentId: readNumberProperty(parsed, 'sourcePaymentId') ?? undefined,
      sourceOriginalAmount: readNumberProperty(parsed, 'sourceOriginalAmount') ?? undefined,
      paidAmount: readNumberProperty(parsed, 'paidAmount') ?? undefined,
      remainderAmount: readNumberProperty(parsed, 'remainderAmount') ?? undefined,
      remainderDate: readStringProperty(parsed, 'remainderDate') ?? undefined,
    };
  } catch {
    return null;
  }
}

export function parsePartialRemainderTransferMetadata(
  payment: Pick<Payment, 'systemNotes'>,
): PartialRemainderTransferMetadata | null {
  const markerLine = (payment.systemNotes ?? '')
    .split('\n')
    .map((line) => line.trim())
    .find((line) => line.startsWith(PARTIAL_REMAINDER_PAYMENT_MARKER) || line.startsWith(PARTIAL_REMAINDER_SOURCE_MARKER));

  if (!markerLine) {
    return null;
  }

  return parsePartialRemainderMarkerLine(markerLine, PARTIAL_REMAINDER_PAYMENT_MARKER)
    ?? parsePartialRemainderMarkerLine(markerLine, PARTIAL_REMAINDER_SOURCE_MARKER);
}

export function isGeneratedInvoiceRemainderPayment(
  payment: Pick<Payment, 'systemNotes'> & { invoiceId?: number | null },
): boolean {
  return Boolean(
    payment.invoiceId
    && payment.invoiceId > 0
    && isTransferredRemainderPayment(payment),
  );
}

export function isInvoiceManagedPayment(
  payment: Pick<Payment, 'documentKind' | 'systemNotes'> & { invoiceId?: number | null },
): boolean {
  return (
    payment.documentKind === 'Invoice'
    || isGeneratedInstallmentPayment(payment)
    || isGeneratedInvoiceRemainderPayment(payment)
  );
}

function getRepresentativeInvoicePayment(payments: Payment[]): Payment | null {
  const invoiceRows = payments.filter((payment) => payment.documentKind === 'Invoice');
  if (invoiceRows.length === 0) {
    return null;
  }

  return [...invoiceRows].sort((left, right) => {
    const amountDiff = (right.amount ?? 0) - (left.amount ?? 0);
    return amountDiff || left.id - right.id;
  })[0] ?? null;
}

function isLegacyBrokenInstallmentAggregate(payments: Payment[]): boolean {
  if (payments.length < 3 || payments.some(isGeneratedInstallmentPayment)) {
    return false;
  }

  const invoiceRows = payments.filter((payment) => payment.documentKind === 'Invoice');
  if (invoiceRows.length < 2) {
    return false;
  }

  const nonInvoiceCount = payments.length - invoiceRows.length;
  if (nonInvoiceCount > 1) {
    return false;
  }

  const representativeInvoice = getRepresentativeInvoicePayment(payments);
  if (!representativeInvoice) {
    return false;
  }

  const maxSiblingAmount = payments
    .filter((payment) => payment.id !== representativeInvoice.id)
    .reduce((maxAmount, payment) => Math.max(maxAmount, payment.amount ?? 0), 0);

  return (representativeInvoice.amount ?? 0) > maxSiblingAmount;
}

function isLegacyInstallmentChildrenSet(payments: Payment[]): boolean {
  if (payments.length < 3 || payments.some(isGeneratedInstallmentPayment)) {
    return false;
  }

  const invoiceRows = payments.filter((payment) => payment.documentKind === 'Invoice');
  if (invoiceRows.length < 2) {
    return false;
  }

  const nonInvoiceCount = payments.length - invoiceRows.length;
  return nonInvoiceCount <= 1;
}

function areAmountsEqual(left: number, right: number): boolean {
  return Math.abs(left - right) <= 0.01;
}

function readNumberProperty(value: unknown, propertyName: string): number | null {
  if (!value || typeof value !== 'object') {
    return null;
  }

  const entry = Object.entries(value as Record<string, unknown>).find(
    ([key]) => key.toLowerCase() === propertyName.toLowerCase(),
  );
  const rawValue = entry?.[1];
  return typeof rawValue === 'number' && Number.isFinite(rawValue) ? rawValue : null;
}

function parseInstallmentToPay(systemNotes?: string | null): number | null {
  const markerLine = (systemNotes ?? '')
    .split('\n')
    .map((line) => line.trim())
    .find((line) => line.startsWith(INSTALLMENT_PAYMENT_MARKER));
  if (!markerLine) {
    return null;
  }

  const rawJson = markerLine.slice(INSTALLMENT_PAYMENT_MARKER.length).trim();
  if (!rawJson.startsWith('{')) {
    return null;
  }

  try {
    return readNumberProperty(JSON.parse(rawJson), 'toPay');
  } catch {
    return null;
  }
}

function hasConsistentInstallmentToPay(payments: Payment[], expectedTotal: number): boolean {
  return payments.some((payment) => {
    const toPay = parseInstallmentToPay(payment.systemNotes);
    return toPay !== null && areAmountsEqual(toPay, expectedTotal);
  });
}

function isConvertedLegacyInstallmentAggregate(payments: Payment[]): boolean {
  if (payments.length < 3) {
    return false;
  }

  const representativeInvoice = getRepresentativeInvoicePayment(payments);
  if (!representativeInvoice || (representativeInvoice.amount ?? 0) <= 0) {
    return false;
  }

  const generatedInstallments = payments.filter(
    (payment) => payment.id !== representativeInvoice.id && isGeneratedInstallmentPayment(payment),
  );
  const generatedInstallmentTotal = generatedInstallments.reduce(
    (sum, payment) => sum + (payment.amount ?? 0),
    0,
  );

  return (
    generatedInstallmentTotal > (representativeInvoice.amount ?? 0)
    && !hasConsistentInstallmentToPay(generatedInstallments, generatedInstallmentTotal)
  );
}

export function getStatusSourcePaymentsForInvoice(payments: Payment[]): Payment[] {
  const representativeInvoice = getRepresentativeInvoicePayment(payments);
  if (representativeInvoice && isConvertedLegacyInstallmentAggregate(payments)) {
    return payments;
  }

  if (representativeInvoice && isLegacyBrokenInstallmentAggregate(payments)) {
    return payments.filter((payment) => payment.id !== representativeInvoice.id);
  }

  if (!representativeInvoice && isLegacyInstallmentChildrenSet(payments)) {
    return payments;
  }

  const linkedPayments = payments.filter((payment) => payment.documentKind !== 'Invoice');
  return linkedPayments.length > 0 ? linkedPayments : payments;
}

export function filterRepresentativeInvoiceWhenInstallmentsExist(payments: Payment[]): Payment[] {
  const representativeInvoice = getRepresentativeInvoicePayment(payments);
  if (!representativeInvoice) {
    return payments;
  }

  if (isConvertedLegacyInstallmentAggregate(payments)) {
    return payments;
  }

  const hasLinkedPaymentRows = payments.some(
    (payment) => payment.id !== representativeInvoice.id && payment.documentKind !== 'Invoice',
  );
  if (!hasLinkedPaymentRows && !payments.some(isGeneratedInstallmentPayment) && !isLegacyBrokenInstallmentAggregate(payments)) {
    return payments;
  }

  return payments.filter((payment) => payment.id !== representativeInvoice.id);
}
