import { useCallback, useEffect, useRef, useState } from 'react';

import { apiService } from '../../services/api';
import type { TaxRegisterJournalPage } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import type { TaskJournalAssignee } from '../../utils/taskJournalStore';

interface TaxRegisterJournalProps {
  companyId: number;
  /** Меняется после действий над записями, чтобы журнал перечитал данные. */
  refreshToken: number;
  isCurrentOwner: () => boolean;
}

const PAGE_SIZE = 20;

const ACTION_LABELS: Record<string, string> = {
  Created: 'Создана',
  Approved: 'Утверждена',
  Corrected: 'Исправлена корректировкой',
  Archived: 'Отправлена в архив',
  YearClosed: 'Год закрыт',
};

const money = (value: number) => value.toLocaleString('ru-RU', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

/**
 * Журнал событий регистра компании: кто и когда создал, утвердил, исправил или архивировал запись.
 * Ответы устаревших запросов отбрасываются, ошибка загрузки даёт кнопку повтора.
 */
export function TaxRegisterJournal({ companyId, refreshToken, isCurrentOwner }: TaxRegisterJournalProps) {
  const [open, setOpen] = useState(false);
  const [filters, setFilters] = useState({ from: '', to: '', action: '' });
  const [page, setPage] = useState(1);
  const [data, setData] = useState<TaxRegisterJournalPage | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [users, setUsers] = useState<TaskJournalAssignee[]>([]);
  const sequence = useRef(0);

  const load = useCallback(async () => {
    if (!isCurrentOwner()) return;
    const current = ++sequence.current;
    try {
      const result = await apiService.getTaxRegisterJournal(companyId, {
        from: filters.from || undefined,
        to: filters.to || undefined,
        action: filters.action || undefined,
        page,
        pageSize: PAGE_SIZE,
      });
      if (current !== sequence.current || !isCurrentOwner()) return;
      setData(result);
      setError(null);
    } catch (loadError) {
      if (current !== sequence.current || !isCurrentOwner()) return;
      setError(extractDisplayErrorMessage(loadError, 'Не удалось загрузить журнал регистра.'));
    }
  }, [companyId, filters, page, isCurrentOwner]);

  useEffect(() => {
    if (!open) return;
    void load();
    const token = sequence;
    return () => {
      token.current++;
    };
  }, [open, load, refreshToken]);

  useEffect(() => {
    if (!open || !isCurrentOwner()) return;
    let cancelled = false;
    void apiService.getTaskJournalAssignees().then(items => {
      if (!cancelled && isCurrentOwner()) setUsers(items);
    }).catch(() => { if (!cancelled && isCurrentOwner()) setUsers([]); });
    return () => { cancelled = true; };
  }, [open, isCurrentOwner]);

  const userName = (id: number) => users.find((item) => item.id === id)?.name ?? (id > 0 ? `Пользователь №${id}` : 'Система');
  const pages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

  return (
    <div className="mt-3" data-testid="tax-register-journal">
      <button type="button" className="text-sm text-blue-700 underline" onClick={() => { if (isCurrentOwner()) setOpen((value) => !value); }}>
        {open ? 'Скрыть журнал событий' : 'Журнал событий'}
      </button>
      {open ? (
        <div className="mt-2 rounded-lg border border-slate-200 p-2 text-sm">
          <div className="flex flex-wrap items-end gap-2">
            <label className="flex flex-col gap-1">
              С
              <input aria-label="Журнал: с" type="date" value={filters.from} onChange={(event) => { setPage(1); setFilters((prev) => ({ ...prev, from: event.target.value })); }} className="rounded-lg border border-slate-300 px-2 py-1" />
            </label>
            <label className="flex flex-col gap-1">
              По
              <input aria-label="Журнал: по" type="date" value={filters.to} onChange={(event) => { setPage(1); setFilters((prev) => ({ ...prev, to: event.target.value })); }} className="rounded-lg border border-slate-300 px-2 py-1" />
            </label>
            <select aria-label="Журнал: действие" value={filters.action} onChange={(event) => { setPage(1); setFilters((prev) => ({ ...prev, action: event.target.value })); }} className="rounded-lg border border-slate-300 px-2 py-1">
              <option value="">Все действия</option>
              {Object.entries(ACTION_LABELS).map(([value, label]) => (
                <option key={value} value={value}>{label}</option>
              ))}
            </select>
          </div>
          {error ? (
            <div role="alert" className="mt-2 text-red-700">
              {error}
              <button type="button" className="ml-2 rounded-lg border px-2 py-0.5" onClick={() => void load()}>Повторить загрузку</button>
            </div>
          ) : null}
          <ul className="mt-2 divide-y divide-slate-100" data-testid="tax-register-journal-list">
            {data?.items.map((item) => (
              <li key={item.eventId} className="py-1">
                {item.createdAt.slice(0, 16).replace('T', ' ')} · {ACTION_LABELS[item.action] ?? item.action} · запись №{item.entryId} ·{' '}
                {item.direction === 'Income' ? 'доход' : 'расход'} {money(item.signedAmount)} · {userName(item.userId)}
                {item.comment ? <span className="text-slate-500"> · {item.comment}</span> : null}
              </li>
            ))}
          </ul>
          {data && data.items.length === 0 ? <p className="py-2 text-slate-500">Событий нет.</p> : null}
          {data && data.total > PAGE_SIZE ? (
            <div className="mt-2 flex items-center gap-2">
              <button type="button" disabled={page <= 1} onClick={() => setPage(page - 1)} className="rounded-lg border px-2 py-0.5 disabled:opacity-50">Назад</button>
              <span>Страница {page} из {pages}</span>
              <button type="button" disabled={page >= pages} onClick={() => setPage(page + 1)} className="rounded-lg border px-2 py-0.5 disabled:opacity-50">Вперёд</button>
            </div>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
