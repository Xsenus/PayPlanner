import { CheckSquare, FileSpreadsheet, Loader2, X } from 'lucide-react';
import { useEffect, useMemo } from 'react';
import { createPortal } from 'react-dom';
import type {
  SpreadsheetImportPreviewChangeDto,
  SpreadsheetImportPreviewDto,
  SpreadsheetImportPreviewRowDto,
  SpreadsheetImportPreviewSectionDto,
} from '../../services/api';
import { TablePagination } from '../ui/TablePagination';

interface SpreadsheetImportPreviewModalProps {
  open: boolean;
  preview: SpreadsheetImportPreviewDto | null;
  activeSectionKey: string;
  selectedPreviewIds: Set<string>;
  counterpartyImportModes: Record<string, string>;
  applyLoading: boolean;
  error?: string | null;
  missingRequiredCounterparties: SpreadsheetImportPreviewRowDto[];
  page: number;
  pageSize: number;
  pageSizeOptions: number[];
  onClose: () => void;
  onSectionChange: (sectionKey: string) => void;
  onPageChange: (page: number) => void;
  onPageSizeChange: (pageSize: number) => void;
  onToggleRow: (row: SpreadsheetImportPreviewRowDto, checked: boolean, shiftKey: boolean) => void;
  onSelectAll: () => void;
  onClearAll: () => void;
  onSelectSection: (section: SpreadsheetImportPreviewSectionDto) => void;
  onClearSection: (section: SpreadsheetImportPreviewSectionDto) => void;
  onSelectRows: (rows: SpreadsheetImportPreviewRowDto[]) => void;
  onClearRows: (rows: SpreadsheetImportPreviewRowDto[]) => void;
  onCounterpartyImportModeChange: (previewId: string, importMode: string) => void;
  onRunImport: () => void;
}

function formatCount(label: string, value: number) {
  return `${label}: ${value}`;
}

function formatPreviewActionLabel(value: string) {
  switch (value) {
    case 'Created':
      return 'Создать';
    case 'Updated':
      return 'Обновить';
    case 'Skipped':
      return 'Пропустить';
    case 'Error':
      return 'Ошибка';
    default:
      return value;
  }
}

function formatChangeKindLabel(value: string) {
  switch (value) {
    case 'Added':
      return 'Новое';
    case 'Updated':
      return 'Изменить';
    case 'Removed':
      return 'Очистить';
    default:
      return value;
  }
}

function badgeClass(value: string) {
  if (value === 'Created') {
    return 'border-emerald-200 bg-emerald-50 text-emerald-700';
  }
  if (value === 'Updated') {
    return 'border-amber-200 bg-amber-50 text-amber-700';
  }
  if (value === 'Skipped') {
    return 'border-slate-200 bg-slate-50 text-slate-600';
  }
  if (value === 'Error') {
    return 'border-red-200 bg-red-50 text-red-700';
  }
  return 'border-slate-200 bg-slate-50 text-slate-600';
}

function changeBadgeClass(value: string) {
  if (value === 'Added') {
    return 'border-emerald-200 bg-emerald-50 text-emerald-700';
  }
  if (value === 'Updated') {
    return 'border-blue-200 bg-blue-50 text-blue-700';
  }
  if (value === 'Removed') {
    return 'border-rose-200 bg-rose-50 text-rose-700';
  }
  return 'border-slate-200 bg-slate-50 text-slate-600';
}

function formatCounterpartyImportModeLabel(value: string) {
  switch (value) {
    case 'LegalEntityWithClient':
      return 'Юрлицо + клиент';
    case 'ClientOnly':
      return 'Только клиент';
    default:
      return value;
  }
}

function buildCounterpartyImportModeHint(row: SpreadsheetImportPreviewRowDto, importMode: string) {
  const normalizedTitle = row.title.trim().toLowerCase();
  const isEntrepreneur = normalizedTitle.startsWith('ип ') || normalizedTitle.startsWith('индивидуальный предприниматель');

  if (isEntrepreneur) {
    return importMode === 'LegalEntityWithClient'
      ? 'ИП будет импортирован как юридическое лицо с привязанной карточкой клиента.'
      : 'ИП будет импортирован только в карточку клиента без отдельной карточки юридического лица.';
  }

  if (importMode === 'LegalEntityWithClient') {
    return 'Контрагент будет импортирован в юридические лица и сразу привязан к клиенту.';
  }

  return row.counterpartyImportModeHint || 'Контрагент будет импортирован только в карточку клиента.';
}

function getRelatedDocumentKey(
  row: SpreadsheetImportPreviewRowDto,
  sectionKey?: string,
) {
  return [
    sectionKey ?? '',
    row.title.trim().toLowerCase(),
    (row.subtitle ?? '').trim().toLowerCase(),
  ].join('::');
}

function getRelatedDocumentPriority(
  row: SpreadsheetImportPreviewRowDto,
  isSelected: boolean,
) {
  let priority = 0;

  if (isSelected) {
    priority += 1000;
  }

  if (row.isSelectable) {
    priority += 100;
  }

  switch (row.action) {
    case 'Created':
      priority += 40;
      break;
    case 'Updated':
      priority += 30;
      break;
    case 'Error':
      priority += 20;
      break;
    case 'Skipped':
      priority += 10;
      break;
    default:
      break;
  }

  return priority;
}

function renderChange(change: SpreadsheetImportPreviewChangeDto, row: SpreadsheetImportPreviewRowDto) {
  return (
    <div key={`${row.previewId}-${change.fieldKey}`} className="rounded-xl border border-white bg-white/90 px-3 py-3 shadow-sm">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500">{change.fieldLabel}</div>
        <span className={`inline-flex rounded-full border px-2 py-1 text-[11px] font-semibold ${changeBadgeClass(change.kind)}`}>
          {formatChangeKindLabel(change.kind)}
        </span>
      </div>
      <div className="mt-3 grid gap-2 md:grid-cols-2">
        <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
          <div className="text-[11px] font-semibold uppercase tracking-[0.18em] text-slate-500">Сейчас</div>
          <div className="mt-1 text-sm text-slate-700">{change.currentValue || '—'}</div>
        </div>
        <div className="rounded-lg border border-blue-100 bg-blue-50 px-3 py-2">
          <div className="text-[11px] font-semibold uppercase tracking-[0.18em] text-blue-600">Будет</div>
          <div className="mt-1 text-sm text-slate-900">{change.incomingValue || '—'}</div>
        </div>
      </div>
    </div>
  );
}

export function SpreadsheetImportPreviewModal({
  open,
  preview,
  activeSectionKey,
  selectedPreviewIds,
  counterpartyImportModes,
  applyLoading,
  error,
  missingRequiredCounterparties,
  page,
  pageSize,
  pageSizeOptions,
  onClose,
  onSectionChange,
  onPageChange,
  onPageSizeChange,
  onToggleRow,
  onSelectAll,
  onClearAll,
  onSelectSection,
  onClearSection,
  onSelectRows,
  onClearRows,
  onCounterpartyImportModeChange,
  onRunImport,
}: SpreadsheetImportPreviewModalProps) {
  useEffect(() => {
    if (!open) {
      return undefined;
    }

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !applyLoading) {
        onClose();
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [applyLoading, onClose, open]);

  useEffect(() => {
    if (!open || typeof document === 'undefined') {
      return undefined;
    }

    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';

    return () => {
      document.body.style.overflow = previousOverflow;
    };
  }, [open]);

  const activeSection = useMemo(
    () => preview?.sections.find((section) => section.key === activeSectionKey) ?? preview?.sections[0] ?? null,
    [activeSectionKey, preview],
  );

  const allRows = useMemo(
    () => preview?.sections.flatMap((section) => section.rows) ?? [],
    [preview],
  );

  const rowById = useMemo(
    () => new Map(allRows.map((row) => [row.previewId, row])),
    [allRows],
  );

  const sectionMetaByPreviewId = useMemo(() => {
    const map = new Map<string, { key: string; label: string; rowIndex: number }>();

    preview?.sections.forEach((section) => {
      section.rows.forEach((row, rowIndex) => {
        map.set(row.previewId, {
          key: section.key,
          label: section.label,
          rowIndex,
        });
      });
    });

    return map;
  }, [preview]);

  const requiredCounterpartiesByDocumentId = useMemo(() => {
    const map = new Map<string, SpreadsheetImportPreviewRowDto[]>();

    (preview?.sections.find((section) => section.key === 'counterparties')?.rows ?? []).forEach((row) => {
      if (row.action !== 'Created' || row.dependencyPreviewIds.length === 0) {
        return;
      }

      row.dependencyPreviewIds.forEach((previewId) => {
        const items = map.get(previewId) ?? [];
        items.push(row);
        map.set(previewId, items);
      });
    });

    return map;
  }, [preview]);

  const totalPages = useMemo(() => {
    if (!activeSection || pageSize <= 0) {
      return 1;
    }

    return Math.max(1, Math.ceil(activeSection.rows.length / pageSize));
  }, [activeSection, pageSize]);

  const pagedRows = useMemo(() => {
    if (!activeSection) {
      return [];
    }

    const start = (page - 1) * pageSize;
    return activeSection.rows.slice(start, start + pageSize);
  }, [activeSection, page, pageSize]);

  function jumpToRow(row: SpreadsheetImportPreviewRowDto) {
    const sectionMeta = sectionMetaByPreviewId.get(row.previewId);
    if (!sectionMeta) {
      return;
    }

    onSectionChange(sectionMeta.key);
    onPageChange(Math.max(1, Math.floor(sectionMeta.rowIndex / pageSize) + 1));
  }

  if (!open || !preview || typeof document === 'undefined') {
    return null;
  }

  const importDisabled = applyLoading || selectedPreviewIds.size === 0 || missingRequiredCounterparties.length > 0;

  return createPortal(
    <div
      role="dialog"
      aria-modal="true"
      className="fixed inset-0 z-[2300] flex items-center justify-center bg-slate-950/55 p-3 sm:p-6"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget && !applyLoading) {
          onClose();
        }
      }}
    >
      <div className="flex max-h-[calc(100vh-1.5rem)] w-full max-w-[1500px] flex-col overflow-hidden rounded-[32px] border border-slate-200 bg-white shadow-2xl">
        <div className="flex flex-col gap-4 border-b border-slate-200 bg-gradient-to-r from-slate-50 to-white px-6 py-5">
          <div className="flex items-start justify-between gap-4">
            <div className="min-w-0">
              <div className="inline-flex items-center gap-2 rounded-full border border-emerald-200 bg-emerald-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.18em] text-emerald-700">
                <FileSpreadsheet className="h-3.5 w-3.5" />
                Предпросмотр импорта
              </div>
              <h2 className="mt-3 text-2xl font-semibold text-slate-900">Подтверждение записей перед загрузкой</h2>
              <p className="mt-1 text-sm text-slate-600">
                Проверьте, какие строки будут созданы или обновлены, какие поля изменятся, и подтвердите только нужные записи.
              </p>
            </div>
            <button
              type="button"
              onClick={onClose}
              disabled={applyLoading}
              className="inline-flex h-11 w-11 items-center justify-center rounded-full border border-slate-200 text-slate-500 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
              aria-label="Закрыть окно предпросмотра"
            >
              <X className="h-5 w-5" />
            </button>
          </div>

          <div className="grid gap-3 xl:grid-cols-[minmax(0,1fr)_300px]">
            <div className="grid gap-3 sm:grid-cols-3">
              <div className="rounded-2xl border border-slate-200 bg-white px-4 py-3">
                <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500">Файлы</div>
                <div className="mt-2 text-2xl font-semibold text-slate-900">{preview.files.length}</div>
                <div className="mt-1 text-sm text-slate-500">Распознано к анализу</div>
              </div>
              <div className="rounded-2xl border border-slate-200 bg-white px-4 py-3">
                <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500">Доступно</div>
                <div className="mt-2 text-2xl font-semibold text-slate-900">{preview.totalSelectableRows}</div>
                <div className="mt-1 text-sm text-slate-500">Строк к подтверждению</div>
              </div>
              <div className="rounded-2xl border border-emerald-200 bg-emerald-50 px-4 py-3">
                <div className="text-xs font-semibold uppercase tracking-[0.18em] text-emerald-700">Выбрано</div>
                <div className="mt-2 text-2xl font-semibold text-emerald-900">{selectedPreviewIds.size}</div>
                <div className="mt-1 text-sm text-emerald-700">Будет отправлено в импорт</div>
              </div>
            </div>

            <div className="flex flex-wrap gap-2 xl:justify-end">
              <button
                type="button"
                onClick={onSelectAll}
                className="rounded-xl border border-slate-300 px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
              >
                Выбрать все
              </button>
              <button
                type="button"
                onClick={onClearAll}
                className="rounded-xl border border-slate-300 px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
              >
                Снять все
              </button>
            </div>
          </div>
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto bg-slate-50/70 px-4 py-4 sm:px-6 sm:py-5">
          {preview.messages.length > 0 && (
            <div className="rounded-2xl border border-slate-200 bg-white px-4 py-4 text-sm text-slate-700 shadow-sm">
              <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500">Сводка анализа</div>
              <div className="mt-2 space-y-1">
                {preview.messages.map((message) => (
                  <div key={message}>{message}</div>
                ))}
              </div>
            </div>
          )}

          {error && (
            <div className="mt-4 rounded-2xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
              {error}
            </div>
          )}

          {missingRequiredCounterparties.length > 0 && (
            <div className="mt-4 rounded-2xl border border-amber-200 bg-amber-50 px-4 py-4 text-sm text-amber-900">
              <div className="font-medium">Для части выбранных документов не хватает обязательных контрагентов.</div>
              <div className="mt-1">
                Выберите в разделе контрагентов: {missingRequiredCounterparties.map((row) => row.title).join(', ')}.
              </div>
            </div>
          )}

          <div className="mt-5 flex flex-wrap gap-2">
            {preview.sections.map((section) => (
              <button
                key={section.key}
                type="button"
                onClick={() => onSectionChange(section.key)}
                className={`rounded-xl border px-4 py-2 text-sm font-semibold transition ${
                  activeSection?.key === section.key
                    ? 'border-emerald-200 bg-emerald-50 text-emerald-900'
                    : 'border-slate-200 bg-white text-slate-700 hover:bg-slate-50'
                }`}
              >
                {section.label} · {section.selectableRows}
              </button>
            ))}
          </div>

          {activeSection && (
            <div className="mt-5 overflow-hidden rounded-[28px] border border-slate-200 bg-white shadow-sm">
              <div className="flex flex-col gap-3 border-b border-slate-200 px-5 py-4">
                <div className="flex flex-wrap items-center gap-2">
                  <h3 className="text-lg font-semibold text-slate-900">{activeSection.label}</h3>
                  <span className="rounded-full border border-slate-200 bg-slate-50 px-3 py-1 text-xs font-medium text-slate-600">
                    {formatCount('Всего строк', activeSection.totalRows)}
                  </span>
                  <span className="rounded-full border border-emerald-200 bg-emerald-50 px-3 py-1 text-xs font-medium text-emerald-700">
                    {formatCount('Создать', activeSection.createRows)}
                  </span>
                  <span className="rounded-full border border-amber-200 bg-amber-50 px-3 py-1 text-xs font-medium text-amber-700">
                    {formatCount('Обновить', activeSection.updateRows)}
                  </span>
                  <span className="rounded-full border border-slate-200 bg-slate-50 px-3 py-1 text-xs font-medium text-slate-600">
                    {formatCount('Пропустить', activeSection.skipRows)}
                  </span>
                  <span className="rounded-full border border-red-200 bg-red-50 px-3 py-1 text-xs font-medium text-red-700">
                    {formatCount('Ошибки', activeSection.errorRows)}
                  </span>
                </div>

                <div className="flex flex-wrap gap-2">
                  <button
                    type="button"
                    onClick={() => onSelectSection(activeSection)}
                    className="rounded-xl border border-slate-300 px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
                  >
                    Выбрать секцию
                  </button>
                  <button
                    type="button"
                    onClick={() => onClearSection(activeSection)}
                    className="rounded-xl border border-slate-300 px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
                  >
                    Очистить секцию
                  </button>
                  <button
                    type="button"
                    onClick={() => onSelectRows(pagedRows)}
                    className="rounded-xl border border-slate-300 px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
                  >
                    Выбрать страницу
                  </button>
                  <button
                    type="button"
                    onClick={() => onClearRows(pagedRows)}
                    className="rounded-xl border border-slate-300 px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
                  >
                    Очистить страницу
                  </button>
                </div>
              </div>

              <div className="space-y-3 px-4 py-4 sm:px-5">
                {pagedRows.map((row) => {
                  const checked = selectedPreviewIds.has(row.previewId);
                  const relatedDocuments = Array.from(
                    row.dependencyPreviewIds
                      .map((previewId) => rowById.get(previewId))
                      .filter((item): item is SpreadsheetImportPreviewRowDto => Boolean(item))
                      .reduce((map, documentRow) => {
                        const documentSection = sectionMetaByPreviewId.get(documentRow.previewId);
                        const key = getRelatedDocumentKey(documentRow, documentSection?.key);
                        const existing = map.get(key);

                        if (!existing) {
                          map.set(key, {
                            row: documentRow,
                            duplicates: [] as SpreadsheetImportPreviewRowDto[],
                          });
                          return map;
                        }

                        existing.duplicates.push(documentRow);
                        const currentPriority = getRelatedDocumentPriority(existing.row, selectedPreviewIds.has(existing.row.previewId));
                        const nextPriority = getRelatedDocumentPriority(documentRow, selectedPreviewIds.has(documentRow.previewId));

                        if (nextPriority > currentPriority) {
                          existing.duplicates.unshift(existing.row);
                          existing.row = documentRow;
                        }

                        return map;
                      }, new Map<string, { row: SpreadsheetImportPreviewRowDto; duplicates: SpreadsheetImportPreviewRowDto[] }>())
                      .values(),
                  );
                  const requiredCounterparties = requiredCounterpartiesByDocumentId.get(row.previewId) ?? [];
                  const effectiveCounterpartyImportMode =
                    counterpartyImportModes[row.previewId]
                    ?? row.counterpartyImportMode
                    ?? row.availableCounterpartyImportModes[0]
                    ?? '';

                  return (
                    <div
                      key={row.previewId}
                      className={`block rounded-3xl border p-4 transition ${
                        checked ? 'border-emerald-200 bg-emerald-50/40' : 'border-slate-200 bg-white'
                      }`}
                    >
                      <div className="flex flex-col gap-4 xl:flex-row xl:items-start xl:justify-between">
                        <div className="flex gap-3">
                          <input
                            type="checkbox"
                            checked={checked}
                            disabled={!row.isSelectable}
                            onChange={(event) => onToggleRow(row, event.target.checked, Boolean((event.nativeEvent as MouseEvent).shiftKey))}
                            className="mt-1 h-4 w-4 rounded border-slate-300 text-emerald-600 focus:ring-emerald-500 disabled:cursor-not-allowed"
                          />
                          <div className="space-y-2">
                            <div className="flex flex-wrap items-center gap-2">
                              <span className="font-semibold text-slate-900">{row.title}</span>
                              <span className={`inline-flex rounded-full border px-2.5 py-1 text-xs font-semibold ${badgeClass(row.action)}`}>
                                {formatPreviewActionLabel(row.action)}
                              </span>
                              {row.isDerived && (
                                <span className="inline-flex rounded-full border border-sky-200 bg-sky-50 px-2.5 py-1 text-xs font-semibold text-sky-700">
                                  Связанный
                                </span>
                              )}
                              {row.changes.length > 0 && (
                                <span className="inline-flex rounded-full border border-blue-200 bg-blue-50 px-2.5 py-1 text-xs font-semibold text-blue-700">
                                  Полей изменится: {row.changes.length}
                                </span>
                              )}
                            </div>

                            {row.subtitle && <div className="text-sm text-slate-600">{row.subtitle}</div>}
                            {row.message && <div className="text-sm text-slate-700">{row.message}</div>}
                            {requiredCounterparties.length > 0 && (
                              <div className="text-xs text-amber-700">
                                Требует контрагента: {requiredCounterparties.map((item) => item.title).join(', ')}
                              </div>
                            )}
                            {row.dependencyTitles.length > 0 && (
                              <div className="text-xs text-sky-700">Связан с документами: {row.dependencyTitles.join(', ')}</div>
                            )}
                            <div className="text-xs text-slate-500">{row.sourceFileName} · строка {row.sourceRowNumber}</div>
                          </div>
                        </div>

                        <div className="grid gap-2 text-sm text-slate-600 sm:grid-cols-2 xl:min-w-[360px]">
                          {row.cells.filter((cell) => cell.value).slice(0, 6).map((cell) => (
                            <div key={`${row.previewId}-${cell.key}`}>
                              <div className="text-[11px] font-semibold uppercase tracking-[0.18em] text-slate-500">{cell.label}</div>
                              <div>{cell.value}</div>
                            </div>
                          ))}
                        </div>
                      </div>

                      {row.availableCounterpartyImportModes.length > 0 && (
                        <div className="mt-4 rounded-2xl border border-violet-100 bg-violet-50/70 p-3">
                          <div className="text-xs font-semibold uppercase tracking-[0.18em] text-violet-700">Режим импорта контрагента</div>
                          <div className="mt-3 flex flex-wrap gap-2">
                            {row.availableCounterpartyImportModes.map((mode) => {
                              const active = effectiveCounterpartyImportMode === mode;
                              return (
                                <button
                                  key={`${row.previewId}-${mode}`}
                                  type="button"
                                  onClick={() => onCounterpartyImportModeChange(row.previewId, mode)}
                                  className={`rounded-xl border px-3 py-2 text-sm font-medium transition ${
                                    active
                                      ? 'border-violet-300 bg-white text-violet-900 shadow-sm'
                                      : 'border-violet-100 bg-white/70 text-slate-700 hover:bg-white'
                                  }`}
                                >
                                  {formatCounterpartyImportModeLabel(mode)}
                                </button>
                              );
                            })}
                          </div>
                          <div className="mt-3 text-sm text-slate-700">
                            {buildCounterpartyImportModeHint(row, effectiveCounterpartyImportMode)}
                          </div>
                        </div>
                      )}

                      {relatedDocuments.length > 0 && (
                        <div className="mt-4 rounded-2xl border border-sky-100 bg-sky-50/70 p-3">
                          <div className="text-xs font-semibold uppercase tracking-[0.18em] text-sky-700">Связанные записи</div>
                          <div className="mt-3 grid gap-2 lg:grid-cols-2">
                            {relatedDocuments.map(({ row: documentRow, duplicates }) => {
                              const documentChecked = selectedPreviewIds.has(documentRow.previewId);
                              const documentSection = sectionMetaByPreviewId.get(documentRow.previewId);
                              const duplicateCount = duplicates.length;

                              return (
                                <div
                                  key={`${row.previewId}-${documentRow.previewId}`}
                                  className={`rounded-xl border px-3 py-3 ${
                                    documentChecked ? 'border-sky-200 bg-white text-slate-900' : 'border-sky-100 bg-white/70 text-slate-700'
                                  }`}
                                >
                                  <div className="flex items-start gap-3">
                                    <input
                                      type="checkbox"
                                      checked={documentChecked}
                                      disabled={!documentRow.isSelectable}
                                      onChange={(event) =>
                                        onToggleRow(
                                          documentRow,
                                          event.target.checked,
                                          Boolean((event.nativeEvent as MouseEvent).shiftKey),
                                        )
                                      }
                                      className="mt-1 h-4 w-4 rounded border-slate-300 text-emerald-600 focus:ring-emerald-500 disabled:cursor-not-allowed"
                                    />
                                    <div className="min-w-0 flex-1">
                                      <div className="flex flex-wrap items-center gap-2">
                                        <span className="font-medium">{documentRow.title}</span>
                                        <span className={`inline-flex rounded-full border px-2 py-1 text-[11px] font-semibold ${badgeClass(documentRow.action)}`}>
                                          {formatPreviewActionLabel(documentRow.action)}
                                        </span>
                                        {documentSection && (
                                          <span className="inline-flex rounded-full border border-slate-200 bg-slate-50 px-2 py-1 text-[11px] font-semibold text-slate-600">
                                            {documentSection.label}
                                          </span>
                                        )}
                                      </div>
                                      {documentRow.subtitle && <div className="mt-1 text-sm text-slate-600">{documentRow.subtitle}</div>}
                                      <div className="mt-2 flex flex-wrap items-center gap-2 text-xs text-slate-500">
                                        <span>{documentChecked ? 'Будет импортирован' : 'Не будет импортирован'}</span>
                                        <span>{documentRow.sourceFileName} · строка {documentRow.sourceRowNumber}</span>
                                      </div>
                                      {duplicateCount > 0 && (
                                        <div className="mt-2 text-xs text-amber-700">
                                          Найдено дублей этого документа: {duplicateCount}. В списке оставлена основная запись.
                                        </div>
                                      )}
                                    </div>
                                    {documentSection && (
                                      <button
                                        type="button"
                                        onClick={() => jumpToRow(documentRow)}
                                        className="rounded-lg border border-slate-300 px-2.5 py-1.5 text-xs font-medium text-slate-700 transition hover:bg-slate-50"
                                      >
                                        Перейти
                                      </button>
                                    )}
                                  </div>
                                </div>
                              );
                            })}
                          </div>
                        </div>
                      )}

                      {row.changes.length > 0 && (
                        <div className="mt-4 rounded-2xl border border-blue-100 bg-blue-50/70 p-3">
                          <div className="text-xs font-semibold uppercase tracking-[0.18em] text-blue-700">Какие поля изменятся</div>
                          <div className="mt-3 grid gap-2 xl:grid-cols-2">
                            {row.changes.map((change) => renderChange(change, row))}
                          </div>
                        </div>
                      )}

                      {row.action === 'Updated' && row.changes.length === 0 && (
                        <div className="mt-4 rounded-2xl border border-slate-200 bg-slate-50 px-3 py-3 text-sm text-slate-600">
                          По основным полям видимых изменений не найдено.
                        </div>
                      )}

                      {row.issues.length > 0 && (
                        <div className="mt-4 rounded-2xl border border-amber-200 bg-amber-50 px-3 py-3 text-sm text-amber-900">
                          {row.issues.map((issue) => (
                            <div key={`${row.previewId}-${issue}`}>{issue}</div>
                          ))}
                        </div>
                      )}
                    </div>
                  );
                })}
              </div>

              <TablePagination
                page={page}
                totalPages={totalPages}
                totalItems={activeSection.rows.length}
                pageSize={pageSize}
                pageSizeOptions={pageSizeOptions}
                onPageChange={onPageChange}
                onPageSizeChange={onPageSizeChange}
                recordsLabel="Строк в секции"
                pageSizeLabel="Строк на странице"
                previousLabel="Назад"
                nextLabel="Вперёд"
              />
            </div>
          )}
        </div>

        <div className="flex flex-col gap-3 border-t border-slate-200 bg-white px-6 py-4 sm:flex-row sm:items-center sm:justify-between">
          <div className="text-sm text-slate-600">
            Выбрано для импорта: <span className="font-semibold text-slate-900">{selectedPreviewIds.size}</span>
          </div>
          <div className="flex flex-col gap-3 sm:flex-row">
            <button
              type="button"
              onClick={onClose}
              disabled={applyLoading}
              className="rounded-xl border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
            >
              Закрыть
            </button>
            <button
              type="button"
              onClick={onRunImport}
              disabled={importDisabled}
              className="inline-flex items-center justify-center gap-2 rounded-xl bg-slate-900 px-5 py-2 text-sm font-semibold text-white transition hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-60"
            >
              {applyLoading ? <Loader2 className="h-4 w-4 animate-spin" /> : <CheckSquare className="h-4 w-4" />}
              Импортировать выбранное
            </button>
          </div>
        </div>
      </div>
    </div>,
    document.body,
  );
}
