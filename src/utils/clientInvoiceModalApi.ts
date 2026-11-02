import type { Invoice, Payment } from '../types';
import { readClientInvoice, requireClientInvoicePayments, UnconfirmedClientInvoiceDataError, type ClientInvoiceReadContext } from './clientInvoiceRead';

export interface ClientInvoiceModalPaymentsParams {
  from?: string;
  to?: string;
  clientId?: number;
  caseId?: number;
  invoiceId?: number;
  excludeInvoiceDocuments?: boolean;
  page?: number;
  pageSize?: number;
  sortBy?: 'date' | 'amount' | 'createdAt';
  sortDir?: 'asc' | 'desc';
}

export interface ClientInvoiceModalLegacyPaymentsParams {
  from?: string;
  to?: string;
  clientId?: number;
  caseId?: number;
}

interface ClientInvoiceModalPaymentsPage {
  items?: Payment[];
  total: number;
  page?: number;
}

export interface ClientInvoiceModalApi {
  getInvoice(invoiceId: number, options?: { signal?: AbortSignal; cache?: RequestCache }): Promise<Invoice>;
  getPayments(params: ClientInvoiceModalLegacyPaymentsParams, options?: { signal?: AbortSignal; cache?: RequestCache }): Promise<Payment[]>;
  getPaymentsV2?(params: ClientInvoiceModalPaymentsParams, options?: { signal?: AbortSignal; cache?: RequestCache }): Promise<ClientInvoiceModalPaymentsPage>;
}

export function bindClientInvoiceModalApi(api: ClientInvoiceModalApi) {
  return {
    fetchInvoice(invoiceId: number, read?: ClientInvoiceReadContext) {
      return readClientInvoice(() => read ? api.getInvoice(invoiceId, { signal: read.signal, cache: 'no-store' }) : api.getInvoice(invoiceId), read);
    },
    async fetchPayments(params: ClientInvoiceModalPaymentsParams, read?: ClientInvoiceReadContext) {
      requireClientInvoicePayments(read);
      if (params.invoiceId && !api.getPaymentsV2) throw new Error('HTTP 501 - Прямой поиск платежей по счёту недоступен.');
      if (!Number.isSafeInteger(params.invoiceId ?? params.clientId) || (params.invoiceId ?? params.clientId ?? 0) <= 0) throw new UnconfirmedClientInvoiceDataError();
      if (params.invoiceId && api.getPaymentsV2) {
        const rows: Payment[] = [];
        let page = params.page ?? 1;
        let total: number | undefined;
        const seen = new Set<number>();
        for (;;) {
          requireClientInvoicePayments(read);
          const query = page === (params.page ?? 1) ? params : { ...params, page };
          const response = await readClientInvoice(() => read
            ? api.getPaymentsV2!(query, { signal: read.signal, cache: 'no-store' }) : api.getPaymentsV2!(query), read);
          requireClientInvoicePayments(read);
          if (!Array.isArray(response?.items) || (response.page !== undefined && response.page !== page)
            || !Number.isSafeInteger(response.total) || response.total < 0
            || (total !== undefined && response.total !== total)) throw new UnconfirmedClientInvoiceDataError();
          total = response.total;
          for (const row of response.items) {
            if (!Number.isSafeInteger(row?.id) || row.id <= 0 || seen.has(row.id)) throw new UnconfirmedClientInvoiceDataError();
            seen.add(row.id); rows.push(row);
          }
          if ((params.page ?? 1) !== 1 || rows.length === total) return rows;
          if (rows.length > total || response.items.length === 0) throw new UnconfirmedClientInvoiceDataError();
          ++page;
        }
      }

      const query = {
        from: params.from,
        to: params.to,
        clientId: params.clientId,
        caseId: params.caseId,
      };
      const rows = await readClientInvoice(() => read ? api.getPayments(query, { signal: read.signal, cache: 'no-store' }) : api.getPayments(query), read);
      requireClientInvoicePayments(read);
      return rows;
    },
  };
}
