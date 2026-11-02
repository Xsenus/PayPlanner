import type { Invoice, Payment } from '../types';
import type { ClientInvoiceModalPaymentsParams } from './clientInvoiceModalApi';
import { readClientInvoice, requireClientInvoicePayments, rethrowFatalClientInvoiceRead, UnconfirmedClientInvoiceDataError, type ClientInvoiceReadContext } from './clientInvoiceRead';
import {
  filterRelatedPaymentsForInvoiceWithOptions,
  filterRepresentativeInvoiceWhenInstallmentsExist,
  getInvoiceEntityId,
} from './invoicePayments';

export const CLIENT_INVOICE_LOOKUP_FROM = '1900-01-01';
export const CLIENT_INVOICE_LOOKUP_TO = '2100-12-31';
export const CLIENT_INVOICE_RELATED_PAYMENTS_PAGE_SIZE = 500;

export function resolveClientInvoiceCaseId(invoice?: Invoice | null): number | undefined {
  if (!invoice) {
    return undefined;
  }

  if (Array.isArray(invoice.clientCaseIds) && invoice.clientCaseIds.length > 1) return undefined;
  if (Array.isArray(invoice.clientCaseIds) && invoice.clientCaseIds.length === 1) {
    const onlyCaseId = invoice.clientCaseIds[0];
    return Number.isSafeInteger(onlyCaseId) && onlyCaseId > 0 ? onlyCaseId : undefined;
  }

  return typeof invoice.clientCaseId === 'number' && Number.isSafeInteger(invoice.clientCaseId) && invoice.clientCaseId > 0
    ? invoice.clientCaseId
    : undefined;
}

export function collectClientInvoiceRelatedPayments(
  invoice: Invoice | null | undefined,
  payments: Payment[],
): Payment[] {
  if (!invoice) {
    return [];
  }

  const entityId = getInvoiceEntityId(invoice);
  // An explicit link to another document cannot be rescued by a matching legacy number.
  const candidates = entityId === null ? payments : payments.filter(payment => !payment.invoiceId || payment.invoiceId === entityId);
  const relatedPayments = filterRelatedPaymentsForInvoiceWithOptions(candidates, invoice, {
    includeCurrentInvoiceRecord: true,
  });

  return filterRepresentativeInvoiceWhenInstallmentsExist(relatedPayments);
}

interface LoadClientInvoiceRelatedPaymentsOptions {
  invoice: Invoice | null | undefined;
  clientId: number;
  visiblePayments?: Payment[];
  fetchPayments: (params: ClientInvoiceModalPaymentsParams, read?: ClientInvoiceReadContext) => Promise<Payment[]>;
  read?: ClientInvoiceReadContext;
}

function assertPaymentRows(rows: Payment[]) {
  if (!Array.isArray(rows) || !rows.every(row => row && Number.isSafeInteger(row.id) && row.id > 0
    && typeof row.isPaid === 'boolean' && (row.type === 'Income' || row.type === 'Expense') && typeof row.status === 'string' && Boolean(row.status)
    && typeof row.description === 'string' && typeof row.amount === 'number' && Number.isFinite(row.amount)
    && (row.paidAmount == null || typeof row.paidAmount === 'number' && Number.isFinite(row.paidAmount))
    && (row.outstandingAmount == null || typeof row.outstandingAmount === 'number' && Number.isFinite(row.outstandingAmount))
    && (row.invoiceId == null || Number.isSafeInteger(row.invoiceId) && row.invoiceId >= 0)
    && typeof row.date === 'string' && Number.isFinite(Date.parse(row.date)))
    || new Set(rows.map(row => row.id)).size !== rows.length) throw new UnconfirmedClientInvoiceDataError('Не удалось подтвердить платежи по счёту. Повторите загрузку.');
}

export async function loadClientInvoiceRelatedPayments({
  invoice,
  clientId,
  fetchPayments,
  read,
}: LoadClientInvoiceRelatedPaymentsOptions): Promise<Payment[]> {
  requireClientInvoicePayments(read);
  if (!invoice) {
    return [];
  }

  if (!Number.isSafeInteger(clientId) || clientId <= 0 || !Number.isSafeInteger(invoice.id) || invoice.id <= 0) throw new UnconfirmedClientInvoiceDataError();
  const caseId = resolveClientInvoiceCaseId(invoice);
  const invoicePaymentId = typeof invoice.id === 'number' && invoice.id > 0 ? invoice.id : undefined;

  if (invoicePaymentId) {
    try {
      const params: ClientInvoiceModalPaymentsParams = {
        invoiceId: invoicePaymentId,
        excludeInvoiceDocuments: false,
        page: 1,
        pageSize: CLIENT_INVOICE_RELATED_PAYMENTS_PAGE_SIZE,
        sortBy: 'date',
        sortDir: 'asc',
      };
      const invoicePaymentPool = await readClientInvoice(() => read ? fetchPayments(params, read) : fetchPayments(params), read);
      requireClientInvoicePayments(read);
      assertPaymentRows(invoicePaymentPool);
      const relatedPayments = collectClientInvoiceRelatedPayments(invoice, invoicePaymentPool);

      if (relatedPayments.length > 0) {
        return relatedPayments;
      }
    } catch (error) {
      rethrowFatalClientInvoiceRead(error, read);
      // A transient direct-read failure may use the existing client lookup, while access remains current.
    }
  }

  requireClientInvoicePayments(read);
  const params: ClientInvoiceModalPaymentsParams = {
    from: CLIENT_INVOICE_LOOKUP_FROM,
    to: CLIENT_INVOICE_LOOKUP_TO,
    clientId,
    caseId,
  };
  const paymentPool = await readClientInvoice(() => read ? fetchPayments(params, read) : fetchPayments(params), read);
  requireClientInvoicePayments(read);
  assertPaymentRows(paymentPool);
  return collectClientInvoiceRelatedPayments(invoice, paymentPool);
}
