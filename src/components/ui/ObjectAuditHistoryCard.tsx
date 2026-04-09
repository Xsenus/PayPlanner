import { Clock3, Loader2, RefreshCw, X } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { apiService, type ObjectAuditEventDto } from '../../services/api';
import {
  formatAuditValue,
  formatAuditChangeSummary,
  getObjectAuditFieldLabel,
  parseAuditChangedFields,
  parseAuditFieldChanges,
} from '../../utils/objectAudit';

interface ObjectAuditHistoryCardProps {
  objectType?: string | null;
  objectId?: number | null;
  compact?: boolean;
}

function formatDateTime(value?: string | null): string {
  if (!value) return 'Неизвестно';
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return value;
  return parsed.toLocaleString('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function getActionLabel(action?: string | null): string {
  switch (action?.toLowerCase()) {
    case 'created':
      return 'Создание';
    case 'updated':
      return 'Изменение';
    case 'deleted':
      return 'Удаление';
    case 'restored':
      return 'Восстановление';
    default:
      return action || 'Событие';
  }
}

function getActionTone(action?: string | null): string {
  switch (action?.toLowerCase()) {
    case 'created':
      return 'border-emerald-200 bg-emerald-50 text-emerald-700';
    case 'updated':
      return 'border-blue-200 bg-blue-50 text-blue-700';
    case 'deleted':
      return 'border-rose-200 bg-rose-50 text-rose-700';
    case 'restored':
      return 'border-teal-200 bg-teal-50 text-teal-700';
    default:
      return 'border-slate-200 bg-slate-50 text-slate-700';
  }
}

function getEventDescription(item: ObjectAuditEventDto): string {
  if (item.action?.toLowerCase() === 'updated') {
    const summary = formatAuditChangeSummary(item.objectType, item.metadataJson);
    if (summary) {
      return summary;
    }
  }

  return item.description?.trim() || 'Служебное событие без подробного описания.';
}

export function ObjectAuditHistoryCard({
  objectType,
  objectId,
  compact = false,
}: ObjectAuditHistoryCardProps) {
  const [items, setItems] = useState<ObjectAuditEventDto[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [isExpanded, setIsExpanded] = useState(false);
  const canLoad = Boolean(objectType && objectId && objectId > 0);

  useEffect(() => {
    if (!canLoad) {
      setItems([]);
      setError(null);
      return;
    }

    let cancelled = false;

    const load = async () => {
      setLoading(true);
      setError(null);
      try {
        const result = await apiService.getObjectAuditHistory(objectType!, objectId!);
        if (!cancelled) {
          setItems(result ?? []);
        }
      } catch (loadError) {
        if (!cancelled) {
          setError(loadError instanceof Error ? loadError.message : 'Не удалось загрузить историю');
        }
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
  }, [canLoad, objectId, objectType]);

  const visibleItems = useMemo(
    () => (compact ? items.slice(0, 6) : items),
    [compact, items],
  );

  const hasMoreItems = compact && items.length > visibleItems.length;

  const timeline = (source: ObjectAuditEventDto[]) => (
    <div className="space-y-3">
      {source.map((item) => (
        (() => {
          const detailedChanges = parseAuditFieldChanges(item.metadataJson);
          const changedFields = parseAuditChangedFields(item.metadataJson).map((fieldName) =>
            getObjectAuditFieldLabel(item.objectType, fieldName),
          );

          return (
            <div
              key={item.id}
              className="rounded-xl border border-slate-200 bg-slate-50/60 px-4 py-3"
            >
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <span
                      className={`inline-flex items-center rounded-full border px-2.5 py-1 text-xs font-semibold ${getActionTone(
                        item.action,
                      )}`}
                    >
                      {getActionLabel(item.action)}
                    </span>
                    <span className="text-xs text-slate-400">{formatDateTime(item.createdAt)}</span>
                  </div>
                  <p className="mt-2 text-sm font-medium text-slate-900">
                    {item.actorUserName?.trim() || 'Система'}
                  </p>
                  <p className="mt-1 text-sm text-slate-600">
                    {getEventDescription(item)}
                  </p>

                  {detailedChanges.length > 0 ? (
                    <div className="mt-3 space-y-2">
                      {detailedChanges.map((change) => (
                        <div
                          key={`${item.id}-${change.field}`}
                          className="rounded-xl border border-slate-200 bg-white px-3 py-2"
                        >
                          <p className="text-xs font-semibold uppercase tracking-[0.2em] text-slate-400">
                            {getObjectAuditFieldLabel(item.objectType, change.field)}
                          </p>
                          <div className="mt-2 grid gap-2 md:grid-cols-2">
                            <div className="rounded-lg bg-slate-50 px-3 py-2">
                              <p className="text-[11px] font-medium uppercase tracking-[0.16em] text-slate-400">
                                Было
                              </p>
                              <p className="mt-1 break-words text-sm text-slate-700">
                                {formatAuditValue(change.oldValue)}
                              </p>
                            </div>
                            <div className="rounded-lg bg-emerald-50/70 px-3 py-2">
                              <p className="text-[11px] font-medium uppercase tracking-[0.16em] text-emerald-500">
                                Стало
                              </p>
                              <p className="mt-1 break-words text-sm text-slate-900">
                                {formatAuditValue(change.newValue)}
                              </p>
                            </div>
                          </div>
                        </div>
                      ))}
                    </div>
                  ) : null}

                  {changedFields.length > 0 && detailedChanges.length === 0 ? (
                    <div className="mt-3 flex flex-wrap gap-2">
                      {changedFields.map((field) => (
                        <span
                          key={`${item.id}-${field}`}
                          className="inline-flex items-center rounded-full border border-slate-200 bg-white px-2.5 py-1 text-xs font-medium text-slate-600"
                        >
                          {field}
                        </span>
                      ))}
                    </div>
                  ) : null}
                </div>
              </div>
            </div>
          );
        })()
      ))}
    </div>
  );

  const refresh = async () => {
    if (!canLoad) return;

    setLoading(true);
    setError(null);
    try {
      const result = await apiService.getObjectAuditHistory(objectType!, objectId!);
      setItems(result ?? []);
    } catch (refreshError) {
      setError(refreshError instanceof Error ? refreshError.message : 'Не удалось обновить историю');
    } finally {
      setLoading(false);
    }
  };

  return (
    <>
      <section className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm">
        <div className="flex items-start justify-between gap-3">
          <div className="flex items-start gap-3">
            <div className="rounded-xl bg-slate-100 p-2 text-slate-600">
              <Clock3 className="h-4 w-4" />
            </div>
            <div>
              <p className="text-sm font-semibold text-slate-900">История объекта</p>
              <p className="mt-1 text-xs text-slate-500">
                Кто и когда создавал, менял или удалял запись.
              </p>
            </div>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            {hasMoreItems ? (
              <button
                type="button"
                onClick={() => setIsExpanded(true)}
                className="inline-flex items-center gap-2 rounded-xl border border-slate-200 px-3 py-2 text-xs font-medium text-slate-600 transition hover:bg-slate-50"
              >
                Показать все
              </button>
            ) : null}
            <button
              type="button"
              onClick={() => void refresh()}
              disabled={!canLoad || loading}
              className="inline-flex items-center gap-2 rounded-xl border border-slate-200 px-3 py-2 text-xs font-medium text-slate-600 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {loading ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RefreshCw className="h-3.5 w-3.5" />}
              Обновить
            </button>
          </div>
        </div>

        {!canLoad ? (
          <div className="mt-4 rounded-xl border border-dashed border-slate-200 bg-slate-50 px-4 py-5 text-sm text-slate-500">
            История появится после сохранения объекта.
          </div>
        ) : error ? (
          <div className="mt-4 rounded-xl border border-rose-200 bg-rose-50 px-4 py-5 text-sm text-rose-700">
            {error}
          </div>
        ) : visibleItems.length === 0 && !loading ? (
          <div className="mt-4 rounded-xl border border-dashed border-slate-200 bg-slate-50 px-4 py-5 text-sm text-slate-500">
            Для этого объекта ещё нет записей истории.
          </div>
        ) : (
          <div className="mt-4">{timeline(visibleItems)}</div>
        )}
      </section>

      {isExpanded && typeof document !== 'undefined'
        ? createPortal(
            <div className="fixed inset-0 z-[2300] flex items-center justify-center bg-slate-900/50 p-4">
              <div className="flex max-h-[85vh] w-full max-w-4xl flex-col overflow-hidden rounded-3xl bg-white shadow-2xl">
                <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4">
                  <div>
                    <h3 className="text-lg font-semibold text-slate-900">Полная история объекта</h3>
                    <p className="mt-1 text-sm text-slate-500">
                      Все зафиксированные изменения по этой записи.
                    </p>
                  </div>
                  <button
                    type="button"
                    onClick={() => setIsExpanded(false)}
                    className="rounded-xl border border-slate-200 p-2 text-slate-500 transition hover:bg-slate-50 hover:text-slate-700"
                    aria-label="Закрыть историю"
                  >
                    <X className="h-4 w-4" />
                  </button>
                </div>
                <div className="overflow-y-auto px-6 py-5">
                  {timeline(items)}
                </div>
              </div>
            </div>,
            document.body,
          )
        : null}
    </>
  );
}
