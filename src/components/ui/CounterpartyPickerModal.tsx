import { Building2, Check, ChevronLeft, ChevronRight, Loader2, Search, UserRoundSearch, X } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { apiService } from '../../services/api';
import type { Client, LegalEntitySummary } from '../../types';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';

export type CounterpartyKind = 'client' | 'legalEntity';

export type CounterpartySelection = {
  kind: CounterpartyKind;
  id: number;
  name: string;
  subtitle?: string | null;
  client?: Client;
  legalEntity?: LegalEntitySummary;
};

type CounterpartyPickerModalProps = {
  open: boolean;
  title?: string;
  mode?: 'single' | 'multiple';
  selected?: CounterpartySelection[];
  initialScope?: CounterpartyKind;
  allowedScopes?: CounterpartyKind[];
  allowEmpty?: boolean;
  emptyLabel?: string;
  disabledKeys?: string[];
  className?: string;
  overlayClassName?: string;
  onClose: () => void;
  onSelect?: (selection: CounterpartySelection | null) => void;
  onApply?: (selection: CounterpartySelection[]) => void;
};

const PAGE_SIZE_OPTIONS = [10, 25, 50];
const DEFAULT_SCOPES: CounterpartyKind[] = ['client', 'legalEntity'];
const EMPTY_SELECTIONS: CounterpartySelection[] = [];

function selectionKey(selection: Pick<CounterpartySelection, 'kind' | 'id'>): string {
  return `${selection.kind}:${selection.id}`;
}

function getLegalEntityDisplayName(entity: LegalEntitySummary): string {
  return entity.shortName?.trim() || entity.fullName?.trim() || `Юрлицо #${entity.id}`;
}

function mapClient(client: Client): CounterpartySelection {
  return {
    kind: 'client',
    id: client.id,
    name: client.name,
    subtitle: client.company || client.legalEntity?.shortName || null,
    client,
  };
}

function mapLegalEntity(entity: LegalEntitySummary): CounterpartySelection {
  return {
    kind: 'legalEntity',
    id: entity.id,
    name: getLegalEntityDisplayName(entity),
    subtitle: [entity.inn ? `ИНН ${entity.inn}` : '', entity.kpp ? `КПП ${entity.kpp}` : '']
      .filter(Boolean)
      .join(' · ') || entity.fullName || null,
    legalEntity: entity,
  };
}

export function CounterpartyPickerModal({
  open,
  title = 'Выбор контрагента',
  mode = 'single',
  selected = EMPTY_SELECTIONS,
  initialScope = 'client',
  allowedScopes = DEFAULT_SCOPES,
  allowEmpty = false,
  emptyLabel = 'Без контрагента',
  disabledKeys = [],
  className = 'max-w-3xl',
  overlayClassName = 'z-[2200]',
  onClose,
  onSelect,
  onApply,
}: CounterpartyPickerModalProps) {
  const normalizedScopes = useMemo(
    () => (allowedScopes.length > 0 ? allowedScopes : DEFAULT_SCOPES),
    [allowedScopes],
  );
  const resolveScope = (value: CounterpartyKind) => (normalizedScopes.includes(value) ? value : normalizedScopes[0]);
  const [scope, setScope] = useState<CounterpartyKind>(() => resolveScope(initialScope));
  const [query, setQuery] = useState('');
  const debouncedQuery = useDebouncedValue(query.trim(), 250);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(PAGE_SIZE_OPTIONS[1]);
  const [items, setItems] = useState<CounterpartySelection[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [draftSelected, setDraftSelected] = useState<CounterpartySelection[]>(selected);

  useEffect(() => {
    if (!open) return;
    setScope(resolveScope(initialScope));
    setQuery('');
    setPage(1);
    setPageSize(PAGE_SIZE_OPTIONS[1]);
    setDraftSelected(selected);
  }, [initialScope, normalizedScopes, open, selected]);

  useEffect(() => {
    setPage(1);
  }, [debouncedQuery, scope, pageSize]);

  useEffect(() => {
    if (!open) return;

    let cancelled = false;
    const load = async () => {
      setLoading(true);
      setError(null);
      try {
        if (scope === 'client') {
          const response = await apiService.getClientsV2({
            search: debouncedQuery || undefined,
            isActive: true,
            sortBy: 'name',
            sortDir: 'asc',
            page,
            pageSize,
          });
          if (cancelled) return;
          setItems((response.items ?? []).map(mapClient));
          setTotal(response.total ?? 0);
          return;
        }

        const response = await apiService.getLegalEntitiesPaged({
          search: debouncedQuery || undefined,
          page,
          pageSize,
        });
        if (cancelled) return;
        setItems((response.items ?? []).map(mapLegalEntity));
        setTotal(response.total ?? 0);
      } catch (loadError) {
        if (cancelled) return;
        setItems([]);
        setTotal(0);
        setError(loadError instanceof Error ? loadError.message : 'Не удалось загрузить контрагентов');
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    };

    void load();
    return () => {
      cancelled = true;
    };
  }, [debouncedQuery, open, page, pageSize, scope]);

  const selectedKeys = useMemo(() => new Set(draftSelected.map(selectionKey)), [draftSelected]);
  const disabledKeySet = useMemo(() => new Set(disabledKeys), [disabledKeys]);
  const totalPages = Math.max(1, Math.ceil(total / Math.max(1, pageSize)));

  const pickSingle = (selection: CounterpartySelection | null) => {
    onSelect?.(selection);
    onClose();
  };

  const toggleMultiple = (selection: CounterpartySelection) => {
    const key = selectionKey(selection);
    setDraftSelected((prev) => {
      if (prev.some((item) => selectionKey(item) === key)) {
        return prev.filter((item) => selectionKey(item) !== key);
      }
      return [...prev, selection];
    });
  };

  const renderAction = (selection: CounterpartySelection) => {
    const key = selectionKey(selection);
    const checked = selectedKeys.has(key);
    const disabled = disabledKeySet.has(key);

    if (mode === 'multiple') {
      return (
        <button
          type="button"
          onClick={() => toggleMultiple(selection)}
          disabled={disabled}
          className={`inline-flex min-w-28 items-center justify-center gap-2 rounded-lg border px-3 py-2 text-sm font-medium transition disabled:cursor-not-allowed disabled:opacity-50 ${
            checked
              ? 'border-blue-200 bg-blue-50 text-blue-700'
              : 'border-slate-200 bg-white text-slate-700 hover:bg-slate-50'
          }`}
        >
          {checked ? <Check className="h-4 w-4" /> : null}
          {checked ? 'Выбрано' : 'Добавить'}
        </button>
      );
    }

    return (
      <button
        type="button"
        onClick={() => pickSingle(selection)}
        disabled={disabled}
        className="inline-flex min-w-28 items-center justify-center gap-2 rounded-lg border border-blue-200 bg-blue-50 px-3 py-2 text-sm font-medium text-blue-700 transition hover:bg-blue-100 disabled:cursor-not-allowed disabled:opacity-50"
      >
        <Check className="h-4 w-4" />
        Выбрать
      </button>
    );
  };

  if (!open) return null;

  return (
    <div className={`fixed inset-0 ${overlayClassName} flex items-center justify-center bg-slate-900/45 p-4`}>
      <div className={`flex h-[calc(100vh-2rem)] max-h-[760px] w-full flex-col overflow-hidden rounded-2xl bg-white shadow-2xl ${className}`}>
        <div className="flex items-center justify-between gap-3 border-b border-slate-200 px-4 py-3">
          <h3 className="text-lg font-semibold text-slate-900">{title}</h3>
          <button
            type="button"
            onClick={onClose}
            className="rounded-full p-1.5 text-slate-500 transition hover:bg-slate-100 hover:text-slate-700"
            aria-label="Закрыть"
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="space-y-3 border-b border-slate-200 p-4">
          {normalizedScopes.length > 1 ? (
            <div className="grid grid-cols-2 rounded-lg border border-slate-200 bg-slate-50 p-1 text-sm font-medium">
              {normalizedScopes.includes('client') ? (
                <button
                  type="button"
                  onClick={() => setScope('client')}
                  className={`inline-flex items-center justify-center gap-2 rounded-md px-3 py-2 transition ${
                    scope === 'client' ? 'bg-white text-blue-700 shadow-sm' : 'text-slate-600 hover:text-slate-900'
                  }`}
                >
                  <UserRoundSearch className="h-4 w-4" />
                  Физические лица
                </button>
              ) : null}
              {normalizedScopes.includes('legalEntity') ? (
                <button
                  type="button"
                  onClick={() => setScope('legalEntity')}
                  className={`inline-flex items-center justify-center gap-2 rounded-md px-3 py-2 transition ${
                    scope === 'legalEntity' ? 'bg-white text-blue-700 shadow-sm' : 'text-slate-600 hover:text-slate-900'
                  }`}
                >
                  <Building2 className="h-4 w-4" />
                  Юридические лица
                </button>
              ) : null}
            </div>
          ) : null}

          <div className="relative">
            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
            <input
              type="search"
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              placeholder={
                scope === 'legalEntity'
                  ? 'Поиск по названию, ИНН, КПП, ОГРН или адресу'
                  : 'Поиск по имени, компании, телефону, почте или статусу'
              }
              className="w-full rounded-lg border border-slate-300 py-2.5 pl-9 pr-3 text-sm text-slate-800 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
            />
          </div>
        </div>

        <div className="min-h-0 flex-1 overflow-auto p-4">
          {allowEmpty && mode === 'single' ? (
            <button
              type="button"
              onClick={() => pickSingle(null)}
              className="mb-3 flex w-full items-center justify-between rounded-xl border border-dashed border-slate-300 bg-slate-50 px-3 py-3 text-left transition hover:bg-slate-100"
            >
              <span>
                <span className="block text-sm font-semibold text-slate-900">{emptyLabel}</span>
                <span className="mt-0.5 block text-xs text-slate-500">Оставить документ без выбранного контрагента.</span>
              </span>
              <span className="rounded-full bg-white px-3 py-1 text-xs font-medium text-slate-600">Выбрать</span>
            </button>
          ) : null}

          {error ? (
            <div className="rounded-xl border border-rose-200 bg-rose-50 px-3 py-4 text-sm text-rose-700">{error}</div>
          ) : loading ? (
            <div className="flex h-full min-h-72 items-center justify-center gap-2 rounded-xl border border-slate-200 px-3 py-10 text-sm text-slate-500">
              <Loader2 className="h-5 w-5 animate-spin" />
              Загрузка контрагентов...
            </div>
          ) : items.length === 0 ? (
            <div className="flex h-full min-h-72 items-center justify-center rounded-xl border border-dashed border-slate-300 bg-slate-50 px-3 py-10 text-center text-sm text-slate-500">
              Контрагенты не найдены
            </div>
          ) : (
            <div className="overflow-hidden rounded-xl border border-slate-200">
              {items.map((selection) => {
                const isClient = selection.kind === 'client';
                const client = selection.client;
                const entity = selection.legalEntity;
                const linkedClients = entity?.clients ?? [];

                return (
                  <div
                    key={selectionKey(selection)}
                    className="flex flex-col gap-3 border-b border-slate-100 px-3 py-3 last:border-0 sm:flex-row sm:items-start sm:justify-between"
                  >
                    <div className="min-w-0">
                      <div className="flex items-center gap-2">
                        {isClient ? (
                          <UserRoundSearch className="h-4 w-4 shrink-0 text-blue-600" />
                        ) : (
                          <Building2 className="h-4 w-4 shrink-0 text-blue-600" />
                        )}
                        <span className="truncate text-sm font-semibold text-slate-900">{selection.name}</span>
                      </div>
                      {selection.subtitle ? (
                        <div className="mt-1 truncate text-xs text-slate-500">{selection.subtitle}</div>
                      ) : null}
                      {client?.clientStatus ? (
                        <div className="mt-2">
                          <ClientStatusBadge status={client.clientStatus} />
                        </div>
                      ) : null}
                      {entity?.fullName && entity.fullName !== entity.shortName ? (
                        <div className="mt-1 line-clamp-1 text-xs text-slate-500">{entity.fullName}</div>
                      ) : null}
                      {linkedClients.length > 0 ? (
                        <div className="mt-1 line-clamp-1 text-xs text-slate-400">
                          Связанные клиенты: {linkedClients.map((item) => item.name).filter(Boolean).slice(0, 4).join(', ')}
                        </div>
                      ) : null}
                    </div>
                    <div className="shrink-0">{renderAction(selection)}</div>
                  </div>
                );
              })}
            </div>
          )}
        </div>

        <div className="flex flex-col gap-3 border-t border-slate-200 px-4 py-3 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex flex-wrap items-center gap-2 text-sm text-slate-500">
            <span>Найдено: {total}</span>
            <select
              value={pageSize}
              onChange={(event) => setPageSize(Number(event.target.value))}
              className="rounded-lg border border-slate-200 bg-white px-2 py-1 text-sm text-slate-700"
            >
              {PAGE_SIZE_OPTIONS.map((option) => (
                <option key={option} value={option}>
                  {option} строк
                </option>
              ))}
            </select>
            {mode === 'multiple' && draftSelected.length > 0 ? (
              <span className="rounded-full bg-blue-50 px-2.5 py-1 text-xs font-medium text-blue-700">
                Выбрано: {draftSelected.length}
              </span>
            ) : null}
          </div>
          <div className="flex items-center justify-end gap-2">
            <button
              type="button"
              onClick={() => setPage((prev) => Math.max(1, prev - 1))}
              disabled={page <= 1 || loading}
              className="inline-flex items-center gap-1 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-600 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
            >
              <ChevronLeft className="h-4 w-4" />
              Назад
            </button>
            <span className="min-w-16 text-center text-sm font-medium text-slate-700">
              {page} / {totalPages}
            </span>
            <button
              type="button"
              onClick={() => setPage((prev) => Math.min(totalPages, prev + 1))}
              disabled={page >= totalPages || loading}
              className="inline-flex items-center gap-1 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-600 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
            >
              Вперед
              <ChevronRight className="h-4 w-4" />
            </button>
            {mode === 'multiple' ? (
              <button
                type="button"
                onClick={() => {
                  onApply?.(draftSelected);
                  onClose();
                }}
                className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white transition hover:bg-blue-700"
              >
                Применить
              </button>
            ) : null}
          </div>
        </div>
      </div>
    </div>
  );
}
