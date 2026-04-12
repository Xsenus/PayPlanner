import type { Invoice, Payment } from '../types';

export interface ClientInvoiceModalPaymentsParams {
  from: string;
  to: string;
  clientId: number;
  caseId?: number;
}

export interface ClientInvoiceModalApi {
  getInvoice(invoiceId: number): Promise<Invoice>;
  getPayments(params: ClientInvoiceModalPaymentsParams): Promise<Payment[]>;
}

export function bindClientInvoiceModalApi(api: ClientInvoiceModalApi) {
  return {
    fetchInvoice(invoiceId: number) {
      return api.getInvoice(invoiceId);
    },
    fetchPayments(params: ClientInvoiceModalPaymentsParams) {
      return api.getPayments(params);
    },
  };
}
