import { useEffect, useMemo, useState } from 'react';
import { ExternalLink, RefreshCw, Search } from 'lucide-react';
import { apiService } from '../../services/api';
import type { ClientCaseCourtSyncJournalEntry } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

const STATUS_OPTIONS = [
  { value: 'all', label: 'Все статусы' },
  { value: 'external-sync-completed', label: 'Обновлено' },
  { value: 'external-sync-error', label: 'Ошибка' },
  { value: 'binding-required', label: 'Нет привязки' },
  { value: 'manual-check-recorded', label: 'Ручная проверка' },
] as const;

const STATUS_LABELS: Record<string, string> = {
  'external-sync-completed': 'Обновлено',
  'external-sync-error': 'Ошибка',
  'binding-required': 'Нет привязки',
  'manual-check-recorded': 'Ручная проверка',
  history: 'История',
};

const STATUS_CLASSES: Record<string, string> = {
  'external-sync-completed': 'border-emerald-200 bg-emerald-50 text-emerald-700',
  'external-sync-error': 'border-rose-200 bg-rose-50 text-rose-700',
  'binding-required': 'border-amber-200 bg-amber-50 text-amber-700',
  'manual-check-recorded': 'border-sky-200 bg-sky-50 text-sky-700',
  history: 'border-slate-200 bg-slate-50 text-slate-700',
};

function formatDateTime(value?: string | null) {
  if (!value) return '—';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '—';
  return new Intl.DateTimeFormat('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  }).format(date);
}

function getStatusLabel(status: string) {
  return STATUS_LABELS[status] ?? status;
}

function getStatusClass(status: string) {
  return STATUS_CLASSES[status] ?? STATUS_CLASSES.history;
}

export function CourtSyncJournalSettings() {
  const [items, setItems] = useState<ClientCaseCourtSyncJournalEntry[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [statusFilter, setStatusFilter] = useState('all');
  const [search, setSearch] = useState('');
  const [appliedSearch, setAppliedSearch] = useState('');

  const loadJournal = async () => {
    setLoading(true);
    setError(null);
    try {
      const result = await apiService.getCourtSyncJournal({
        take: 50,
        status: statusFilter === 'all' ? undefined : statusFilter,
        search: appliedSearch.trim() || undefined,
      });
      setItems(result ?? []);
    } catch (loadError) {
      setError(extractDisplayErrorMessage(loadError, 'Не удалось загрузить журнал судебной синхронизации.'));
      setItems([]);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void loadJournal();
  }, [statusFilter, appliedSearch]);

  const counters = useMemo(() => ({
    total: items.length,
    errors: items.filter((item) => item.status === 'external-sync-error').length,
    bindingRequired: items.filter((item) => item.status === 'binding-required').length,
    completed: items.filter((item) => item.status === 'external-sync-completed').length,
  }), [items]);

  const applySearch = () => {
    setAppliedSearch(search.trim());
  };

  return (
    <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
      <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
        <div>
          <h4 className="text-xl font-semibold text-gray-900">Журнал судебной синхронизации</h4>
          <p className="mt-1 text-sm text-gray-600">
            Последние проверки судебных источников по делам, доступным текущему пользователю.
          </p>
        </div>
        <button
          type="button"
          onClick={() => void loadJournal()}
          disabled={loading}
          className="inline-flex h-10 items-center justify-center gap-2 rounded-lg border border-gray-300 px-4 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
        >
          <RefreshCw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} />
          <span>{loading ? 'Обновляем' : 'Обновить'}</span>
        </button>
      </div>

      <div className="mt-5 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <Metric label="Записей" value={counters.total} />
        <Metric label="Обновлено" value={counters.completed} />
        <Metric label="Ошибок" value={counters.errors} />
        <Metric label="Без привязки" value={counters.bindingRequired} />
      </div>

      <div className="mt-5 flex flex-col gap-3 lg:flex-row lg:items-center">
        <select
          value={statusFilter}
          onChange={(event) => setStatusFilter(event.target.value)}
          aria-label="Статус судебной синхронизации"
          className="h-10 rounded-lg border border-gray-300 bg-white px-3 text-sm text-gray-700 outline-none focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
        >
          {STATUS_OPTIONS.map((option) => (
            <option key={option.value} value={option.value}>{option.label}</option>
          ))}
        </select>
        <div className="flex min-w-0 flex-1 gap-2">
          <div className="relative min-w-0 flex-1">
            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-gray-400" />
            <input
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === 'Enter') applySearch();
              }}
              placeholder="Поиск по делу, суду, номеру или сообщению"
              className="h-10 w-full rounded-lg border border-gray-300 pl-9 pr-3 text-sm outline-none focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
            />
          </div>
          <button
            type="button"
            onClick={applySearch}
            className="inline-flex h-10 items-center justify-center rounded-lg bg-slate-900 px-4 text-sm font-medium text-white hover:bg-slate-800"
          >
            Найти
          </button>
        </div>
      </div>

      {error ? (
        <div className="mt-5 rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{error}</div>
      ) : null}

      <div className="mt-5 overflow-hidden rounded-2xl border border-gray-200">
        <div className="overflow-x-auto">
          <table className="min-w-full divide-y divide-gray-200 text-sm">
            <thead className="bg-gray-50 text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
              <tr>
                <th className="px-4 py-3">Дата</th>
                <th className="px-4 py-3">Статус</th>
                <th className="px-4 py-3">Дело</th>
                <th className="px-4 py-3">Суд</th>
                <th className="px-4 py-3">Сообщение</th>
                <th className="px-4 py-3">Инициатор</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100 bg-white">
              {loading && items.length === 0 ? (
                <tr>
                  <td colSpan={6} className="px-4 py-8 text-center text-sm text-gray-500">Загрузка журнала...</td>
                </tr>
              ) : items.length === 0 ? (
                <tr>
                  <td colSpan={6} className="px-4 py-8 text-center text-sm text-gray-500">Записей судебной синхронизации не найдено.</td>
                </tr>
              ) : items.map((item) => (
                <tr key={item.id} className="align-top">
                  <td className="whitespace-nowrap px-4 py-3 text-gray-600">{formatDateTime(item.occurredAt)}</td>
                  <td className="px-4 py-3">
                    <span className={`inline-flex rounded-full border px-2.5 py-1 text-xs font-semibold ${getStatusClass(item.status)}`}>
                      {getStatusLabel(item.status)}
                    </span>
                  </td>
                  <td className="px-4 py-3">
                    <div className="font-semibold text-gray-900">{item.clientCaseTitle || `Дело #${item.clientCaseId}`}</div>
                    <div className="mt-1 text-xs text-gray-500">{item.ownerName || '—'}{item.caseNumber ? ` · ${item.caseNumber}` : ''}</div>
                    <a
                      href={`/cases/${item.clientCaseId}`}
                      className="mt-2 inline-flex items-center gap-1 text-xs font-semibold text-blue-700 hover:text-blue-800"
                    >
                      Открыть дело
                      <ExternalLink className="h-3.5 w-3.5" />
                    </a>
                  </td>
                  <td className="px-4 py-3 text-gray-600">
                    <div>{item.courtName || '—'}</div>
                    <div className="mt-1 text-xs text-gray-500">{item.courtCaseNumber || 'Номер не указан'}</div>
                  </td>
                  <td className="max-w-xl px-4 py-3 text-gray-700">{item.message}</td>
                  <td className="whitespace-nowrap px-4 py-3 text-gray-600">{item.actorName || '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}

function Metric({ label, value }: { label: string; value: number }) {
  return (
    <div className="rounded-2xl border border-gray-200 bg-gray-50 px-4 py-3">
      <div className="text-xs font-semibold uppercase tracking-wide text-gray-500">{label}</div>
      <div className="mt-1 text-2xl font-semibold text-gray-900">{value}</div>
    </div>
  );
}
