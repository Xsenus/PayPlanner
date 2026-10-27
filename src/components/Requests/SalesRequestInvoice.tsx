import { useEffect, useRef, useState } from 'react';

import { apiService, type SalesRequestInvoiceCandidate } from '../../services/api';
import type { SalesRequestItem } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

interface Props {
  requestId: number;
  version: number;
  canEdit: boolean;
  onSaved: (item: SalesRequestItem, previousVersion: number) => void;
}

const label = (item: SalesRequestInvoiceCandidate) =>
  `№${item.number} от ${item.date.slice(0, 10)} · организация №${item.sellerCompanyId}${item.sellerBranchId ? ` · подразделение №${item.sellerBranchId}` : ''} · запись №${item.invoiceId}`;

export function SalesRequestInvoice({ requestId, version, canEdit, onSaved }: Props) {
  const [open, setOpen] = useState(false);
  const [state, setState] = useState<'loading' | 'none' | 'available' | 'unavailable' | 'error'>('loading');
  const [invoice, setInvoice] = useState<SalesRequestInvoiceCandidate | null>(null);
  const [allocation, setAllocation] = useState<number | null>(null);
  const [shared, setShared] = useState(false);
  const [amount, setAmount] = useState('');
  const [query, setQuery] = useState('');
  const [items, setItems] = useState<SalesRequestInvoiceCandidate[]>([]);
  const [hasMore, setHasMore] = useState(false);
  const [searching, setSearching] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const sequence = useRef(0);
  const searchSequence = useRef(0);

  useEffect(() => () => { sequence.current++; searchSequence.current++; }, []);

  const load = async () => {
    const current = ++sequence.current;
    setOpen(true); setState('loading'); setError('');
    try {
      const result = await apiService.getSalesRequestServiceInvoice(requestId);
      if (current !== sequence.current) return;
      const value = result.allocationAmount ?? null;
      setInvoice(result.invoice); setState(result.state); setAllocation(value); setShared(value !== null); setAmount(value === null ? '' : String(value));
    } catch (cause) {
      if (current !== sequence.current) return;
      setState('error'); setError(extractDisplayErrorMessage(cause, 'Не удалось загрузить счёт.'));
    }
  };

  const search = async (afterInvoiceId = 0) => {
    const current = ++searchSequence.current;
    const text = query.trim();
    setSearching(true); setError('');
    try {
      const page = await apiService.getSalesRequestInvoiceCandidates(requestId, text, afterInvoiceId);
      if (current !== searchSequence.current) return;
      setItems((previous) => afterInvoiceId ? [...previous, ...page.items] : page.items);
      setHasMore(page.hasMore);
    } catch (cause) {
      if (current === searchSequence.current) setError(extractDisplayErrorMessage(cause, 'Не удалось найти счета.'));
    } finally {
      if (current === searchSequence.current) setSearching(false);
    }
  };

  const save = async (selected: SalesRequestInvoiceCandidate | null) => {
    let selectedAmount: number | null = null;
    if (selected && shared) {
      const normalized = amount.trim().replace(',', '.');
      selectedAmount = Number(normalized);
      if (!/^\d+(?:\.\d{1,2})?$/.test(normalized) || !Number.isFinite(selectedAmount) || selectedAmount <= 0 || selectedAmount > 1_000_000_000_000) {
        setError('Укажите долю заявки от 0,01 до 1 000 000 000 000 ₽ с точностью до копеек.');
        return;
      }
    }
    const current = sequence.current;
    const previousVersion = version;
    setSaving(true); setError('');
    try {
      const updated = await apiService.setSalesRequestServiceInvoice(requestId, previousVersion, selected?.invoiceId ?? null, selectedAmount);
      if (current !== sequence.current) return;
      setInvoice(selected); setState(selected ? 'available' : 'none'); setAllocation(selectedAmount); setItems([]); setHasMore(false);
      onSaved(updated, previousVersion);
    } catch (cause) {
      if (current === sequence.current) setError(extractDisplayErrorMessage(cause, 'Не удалось сохранить счёт. Обновите заявку и попробуйте ещё раз.'));
    } finally {
      if (current === sequence.current) setSaving(false);
    }
  };

  if (!open) return <button type="button" className="mt-2 text-sm text-blue-700 underline" onClick={() => void load()}>Счёт услуги заявки</button>;
  return (
    <section className="mt-2 rounded-lg border border-slate-200 p-2 text-sm" aria-label="Счёт услуги заявки">
      <div className="flex justify-between gap-2"><strong>Счёт услуги заявки</strong><button type="button" disabled={saving} onClick={() => { sequence.current++; searchSequence.current++; setOpen(false); setItems([]); setHasMore(false); }}>Закрыть счёт</button></div>
      {state === 'loading' ? <p>Загрузка счёта…</p> : null}
      {state === 'none' ? <p>Счёт не выбран.</p> : null}
      {state === 'unavailable' ? <p>Связанный счёт недоступен. Обратитесь к сотруднику с доступом к счёту.</p> : null}
      {state === 'available' && invoice ? <p>{label(invoice)}</p> : null}
      {state === 'available' && invoice ? <p className="text-slate-600">{allocation === null ? 'Отдельный счёт для заявки.' : `Общий счёт · доля заявки: ${allocation.toLocaleString('ru-RU')} ₽.`}</p> : null}
      {error ? <p role="alert" className="text-rose-700">{error}</p> : null}
      {canEdit && state !== 'loading' && state !== 'error' && state !== 'unavailable' ? (
        <div className="mt-2 space-y-2">
          <fieldset className="flex flex-wrap gap-3" disabled={saving}>
            <legend>Как использовать счёт</legend>
            <label className="flex items-center gap-1"><input type="radio" name="request-invoice-mode" checked={!shared} onChange={() => setShared(false)} /> Только для этой заявки</label>
            <label className="flex items-center gap-1"><input type="radio" name="request-invoice-mode" checked={shared} onChange={() => setShared(true)} /> Общий для нескольких заявок</label>
          </fieldset>
          {shared ? <label className="flex flex-col gap-1">Доля этой заявки, ₽<input aria-label="Доля этой заявки" inputMode="decimal" value={amount} onChange={(event) => setAmount(event.target.value)} className="w-44 rounded border border-slate-300 px-2 py-1" /></label> : null}
          <div className="flex flex-wrap gap-2">
            <input aria-label="Поиск счёта услуги" maxLength={100} value={query} onChange={(event) => { searchSequence.current++; setQuery(event.target.value); setItems([]); setHasMore(false); setSearching(false); }} className="rounded border border-slate-300 px-2 py-1" />
            <button type="button" disabled={searching || saving} onClick={() => void search()} className="rounded border border-slate-300 px-2 py-1">Найти счёт</button>
            {invoice ? <button type="button" disabled={saving || searching} onClick={() => void save(null)} className="rounded border border-slate-300 px-2 py-1">Убрать связь</button> : null}
            {invoice ? <button type="button" disabled={saving || searching} onClick={() => void save(invoice)} className="rounded border border-slate-300 px-2 py-1">Сохранить режим счёта</button> : null}
          </div>
          {items.map((item) => <div key={item.invoiceId} className="flex flex-wrap items-center gap-2"><span>{label(item)}</span><button type="button" disabled={saving || searching} onClick={() => void save(item)} className="text-blue-700 underline">Выбрать</button></div>)}
          {hasMore ? <button type="button" disabled={searching || saving} onClick={() => void search(items.at(-1)?.invoiceId ?? 0)} className="text-blue-700 underline">Показать ещё</button> : null}
        </div>
      ) : null}
    </section>
  );
}
