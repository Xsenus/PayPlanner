import type { Invoice } from '../types';
import { awaitOwnedUiResult, StaleUiOperationError } from './StaleUiOperationError';
import { getInvoiceEntityId } from './invoicePayments';

export type ClientInvoiceReadContext = {
  signal?: AbortSignal;
  isCurrent?: () => boolean;
  canReadPayments?: () => boolean;
};

export class UnconfirmedClientInvoiceDataError extends Error {
  constructor(message = 'Не удалось подтвердить данные счёта. Повторите загрузку.') {
    super(message);
    this.name = 'UnconfirmedClientInvoiceDataError';
  }
}

export const isClientInvoiceReadCurrent = (read?: ClientInvoiceReadContext) =>
  !read?.signal?.aborted && (read?.isCurrent?.() ?? true);

export function readClientInvoice<T>(work: () => Promise<T>, read?: ClientInvoiceReadContext) {
  return awaitOwnedUiResult(work, () => isClientInvoiceReadCurrent(read));
}

export function requireClientInvoicePayments(read?: ClientInvoiceReadContext) {
  if (!isClientInvoiceReadCurrent(read)) throw new StaleUiOperationError();
  if (read?.canReadPayments?.() === false) throw new Error('HTTP 403 - Недостаточно прав для просмотра платежей.');
}

export function rethrowFatalClientInvoiceRead(error: unknown, read?: ClientInvoiceReadContext) {
  if (!isClientInvoiceReadCurrent(read)) throw new StaleUiOperationError();
  if (error instanceof StaleUiOperationError || error instanceof UnconfirmedClientInvoiceDataError
    || (error instanceof Error && error.name === 'AbortError')
    || /(?:^|\s)HTTP\s+4\d{2}(?:\s|$)/i.test(error instanceof Error ? error.message : String(error ?? ''))) throw error;
}

export function assertFreshClientInvoice(previous: Invoice, fresh: Invoice, clientId: number) {
  const entityId = getInvoiceEntityId(previous);
  const sameInvoice = entityId !== null && Number.isSafeInteger(entityId) && entityId === getInvoiceEntityId(fresh);
  const participants = [fresh?.clientId, fresh?.expenseClientId, fresh?.legalEntityId, fresh?.expenseLegalEntityId];
  const owners = [fresh?.clientId, fresh?.expenseClientId].filter(id => typeof id === 'number' && id > 0);
  if (!Number.isSafeInteger(clientId) || clientId <= 0 || !Number.isSafeInteger(fresh?.id) || fresh.id <= 0 || !Number.isFinite(fresh.amount)
    || participants.some(id => id != null && (!Number.isSafeInteger(id) || id < 0))
    || (fresh.id !== previous.id && !sameInvoice) || (owners.length > 0 && !owners.includes(clientId))) {
    throw new UnconfirmedClientInvoiceDataError('Ответ относится к другому счёту или клиенту. Откройте счёт заново.');
  }
}
