import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { Invoice, Payment } from '../types';
import { apiService } from '../services/api';
import { bindClientInvoiceModalApi } from '../utils/clientInvoiceModalApi';
import { loadClientInvoiceRelatedPayments } from '../utils/clientInvoiceRelatedPayments';
import { refreshClientInvoiceModalData } from '../utils/clientInvoiceModalData';
import { assertFreshClientInvoice } from '../utils/clientInvoiceRead';
import { extractDisplayErrorMessage } from '../utils/errorUtils';
import { StaleUiOperationError } from '../utils/StaleUiOperationError';
import { useDocumentDeliveryOwner, type DocumentDeliveryRead, type DocumentReadOwner } from './useDocumentDeliveryOwner';

const EMPTY_PAYMENTS: Payment[] = [];
const DENIED = 'Недостаточно прав для просмотра платежей.';
type Options = {
  enabled: boolean;
  owner: DocumentReadOwner;
  canReadInvoice: () => boolean;
  canReadPayments: () => boolean;
  refreshInvoices: () => Promise<unknown>;
  refreshPayments: () => Promise<unknown>;
  onInvoiceRefreshed: (invoice: Invoice) => void;
};
type View = { scope: string; owner: DocumentDeliveryRead; rows: Payment[]; loading: boolean; error: string | null; confirmed: boolean };

export function useClientInvoiceModalReads(clientId: number, invoice: Invoice | null, options: Options) {
  const enabled = options.enabled && Number.isSafeInteger(invoice?.id) && (invoice?.id ?? 0) > 0;
  const { capture, scope } = useDocumentDeliveryOwner(JSON.stringify([options.owner.scope, enabled, clientId, invoice?.id, invoice?.invoiceEntityId, invoice?.number, invoice?.date, invoice?.clientCaseId, invoice?.clientCaseIds]));
  const externalCapture = options.owner.capture;
  const latest = useRef({ invoice, options }); latest.current = { invoice, options };
  const api = useMemo(() => bindClientInvoiceModalApi(apiService), []);
  const [view, setView] = useState<View | null>(null);
  const confirmation = useRef<{ scope: string; owner: DocumentDeliveryRead } | null>(null);
  const lastMode = useRef({ scope, mode: 'payments' as 'payments' | 'refresh' });
  const request = useRef<{ sequence: number; controller: AbortController | null; promise: Promise<void> | null }>({ sequence: 0, controller: null, promise: null });
  const cancel = useCallback(() => {
    confirmation.current = null;
    ++request.current.sequence; request.current.controller?.abort(); request.current.controller = null; request.current.promise = null;
  }, []);
  const run = useCallback((mode: 'payments' | 'refresh', singleFlight = false): Promise<void> => {
    const owner = enabled ? capture() : null; const external = externalCapture();
    const ownsForm = () => !!owner?.isCurrent() && !!external?.isCurrent() && latest.current.options.canReadInvoice();
    if (!owner || !ownsForm()) return Promise.reject(new StaleUiOperationError());
    if (singleFlight && request.current.controller && !request.current.controller.signal.aborted && request.current.promise) return request.current.promise;
    lastMode.current = { scope, mode };
    cancel(); const sequence = request.current.sequence; const controller = new AbortController(); request.current.controller = controller;
    const current = () => ownsForm() && sequence === request.current.sequence && !controller.signal.aborted;
    const read = { signal: controller.signal, isCurrent: current, canReadPayments: () => latest.current.options.canReadPayments() };
    const snapshot = latest.current.invoice!;
    setView({ scope, owner, rows: EMPTY_PAYMENTS, loading: true, error: null, confirmed: false });
    const work = (async () => {
      try {
        assertFreshClientInvoice(snapshot, snapshot, clientId);
        let rows: Payment[];
        if (mode === 'refresh') {
          const result = await refreshClientInvoiceModalData({ selectedInvoice: snapshot, clientId, visiblePayments: EMPTY_PAYMENTS,
            refreshInvoices: () => latest.current.options.refreshInvoices(), refreshPayments: () => latest.current.options.refreshPayments(),
            fetchInvoice: api.fetchInvoice, fetchPayments: api.fetchPayments, read });
          if (!current()) throw new StaleUiOperationError();
          if (!result.invoice) throw new Error('Не удалось подтвердить счёт. Повторите загрузку.');
          assertFreshClientInvoice(snapshot, result.invoice, clientId);
          rows = result.relatedPayments;
          latest.current.options.onInvoiceRefreshed(result.invoice);
        } else {
          if (!latest.current.options.canReadPayments()) throw new Error('HTTP 403 - ' + DENIED);
          rows = await loadClientInvoiceRelatedPayments({ invoice: snapshot, clientId, fetchPayments: api.fetchPayments, read });
        }
        if (current()) {
          const allowed = latest.current.options.canReadPayments();
          confirmation.current = allowed ? { scope, owner } : null;
          setView({ scope, owner, rows: allowed ? rows : EMPTY_PAYMENTS, loading: false, error: allowed ? null : DENIED, confirmed: allowed });
        }
      } catch (error) {
        if (current()) {
          setView({ scope, owner, rows: EMPTY_PAYMENTS, loading: false, confirmed: false,
            error: latest.current.options.canReadPayments() ? extractDisplayErrorMessage(error, 'Не удалось загрузить платежи по счёту.') : DENIED });
          controller.abort();
        }
        throw error;
      } finally {
        if (request.current.controller === controller) { request.current.controller = null; request.current.promise = null; }
      }
    })();
    if (request.current.controller === controller) request.current.promise = work;
    return work;
  }, [enabled, capture, externalCapture, scope, clientId, api, cancel]);
  useEffect(() => { if (enabled) void run('payments').catch(() => undefined); return cancel; }, [enabled, run, cancel]);
  const refresh = useCallback(() => run('refresh'), [run]);
  const retry = useCallback(() => run(lastMode.current.scope === scope ? lastMode.current.mode : 'payments', true), [run, scope]);
  const cancelCurrent = useCallback(() => { if (capture() && externalCapture()) cancel(); }, [capture, externalCapture, cancel]);
  const isReady = useCallback(() => Boolean(enabled && capture() && externalCapture() && latest.current.options.canReadInvoice()
    && latest.current.options.canReadPayments() && confirmation.current?.scope === scope && confirmation.current.owner.isCurrent()
    && !request.current.controller), [enabled, capture, externalCapture, scope]);
  const canView = enabled && options.canReadInvoice() && Boolean(externalCapture());
  const visible = canView && view?.scope === scope && view.owner.isCurrent() ? view : null;
  const denied = canView && !options.canReadPayments();
  return {
    rows: !denied && visible?.confirmed && !visible.loading && !visible.error ? visible.rows : EMPTY_PAYMENTS,
    loading: canView && !denied && (visible?.loading ?? true),
    error: denied ? DENIED : visible?.error ?? null,
    ready: isReady(), isReady,
    refresh, retry, cancel: cancelCurrent,
  };
}
