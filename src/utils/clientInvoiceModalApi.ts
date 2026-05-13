import type { Invoice, Payment } from '../types';

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
}

export interface ClientInvoiceModalApi {
  getInvoice(invoiceId: number): Promise<Invoice>;
  getPayments(params: ClientInvoiceModalLegacyPaymentsParams): Promise<Payment[]>;
  getPaymentsV2?(params: ClientInvoiceModalPaymentsParams): Promise<ClientInvoiceModalPaymentsPage>;
}

export function bindClientInvoiceModalApi(api: ClientInvoiceModalApi) {
  return {
    fetchInvoice(invoiceId: number) {
      return api.getInvoice(invoiceId);
    },
    async fetchPayments(params: ClientInvoiceModalPaymentsParams) {
      if (params.invoiceId && api.getPaymentsV2) {
        const response = await api.getPaymentsV2(params);
        return response.items ?? [];
      }

      return api.getPayments({
        from: params.from,
        to: params.to,
        clientId: params.clientId,
        caseId: params.caseId,
      });
    },
  };
}
