import { useCallback, useEffect, useMemo, useState } from 'react';
import { CheckSquare, FileSpreadsheet, History, Loader2, RefreshCw, RotateCcw } from 'lucide-react';
import { apiService } from '../../services/api';
import type {
  DatabaseImportResultDto,
  DatabaseStatusDto,
  SpreadsheetImportBatchDetailDto,
  SpreadsheetImportBatchSummaryDto,
  SpreadsheetImportPreviewDto,
  SpreadsheetImportPreviewRowDto,
  SpreadsheetImportPreviewSectionDto,
} from '../../services/api';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { SpreadsheetImportPreviewModal } from './SpreadsheetImportPreviewModal';

const PREVIEW_PAGE_SIZE_OPTIONS = [10, 25, 50, 100];

function formatDateTime(value?: string | null) {
  if (!value) return '—';
  try {
    return new Date(value).toLocaleString('ru-RU');
  } catch {
    return value;
  }
}

function formatCount(label: string, value: number) {
  return `${label}: ${value}`;
}

function formatOperationActionLabel(value: string) {
  switch (value) {
    case 'Created':
      return 'Создано';
    case 'Updated':
      return 'Обновлено';
    case 'Skipped':
      return 'Пропущено';
    case 'Error':
      return 'Ошибка';
    default:
      return value;
  }
}

function formatRollbackActionLabel(value: string) {
  switch (value) {
    case 'Deleted':
      return 'Удалено';
    case 'Restored':
      return 'Восстановлено';
    case 'Skipped':
      return 'Пропущено';
    case 'Error':
      return 'Ошибка';
    default:
      return value;
  }
}

function formatBatchStatusLabel(value: string) {
  switch (value) {
    case 'Applied':
      return 'Импорт выполнен';
    case 'AppliedWithErrors':
      return 'Импорт с ошибками';
    case 'RolledBack':
      return 'Откат выполнен';
    case 'RolledBackWithConflicts':
      return 'Откат с конфликтами';
    default:
      return value;
  }
}

function formatSectionLabel(value: string) {
  switch (value) {
    case 'counterparties':
      return 'Контрагенты';
    case 'invoices':
      return 'Счета';
    case 'acts':
      return 'Акты';
    default:
      return value;
  }
}

function badgeClass(value: string) {
  if (value === 'Created' || value === 'Applied' || value === 'RolledBack' || value === 'Deleted' || value === 'Restored') {
    return 'border-emerald-200 bg-emerald-50 text-emerald-700';
  }
  if (value === 'Updated' || value === 'AppliedWithErrors' || value === 'RolledBackWithConflicts') {
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

export function SpreadsheetImportSettings() {
  const toast = useToast();
  const { confirm } = useDialog();
  const [files, setFiles] = useState<File[]>([]);
  const [preview, setPreview] = useState<SpreadsheetImportPreviewDto | null>(null);
  const [previewModalOpen, setPreviewModalOpen] = useState(false);
  const [activeSectionKey, setActiveSectionKey] = useState<string>('');
  const [previewPage, setPreviewPage] = useState(1);
  const [previewPageSize, setPreviewPageSize] = useState(PREVIEW_PAGE_SIZE_OPTIONS[1]);
  const [selectedPreviewIds, setSelectedPreviewIds] = useState<Set<string>>(new Set());
  const [counterpartyImportModes, setCounterpartyImportModes] = useState<Record<string, string>>({});
  const [lastSelectedPreviewId, setLastSelectedPreviewId] = useState<string | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [applyLoading, setApplyLoading] = useState(false);
  const [historyLoading, setHistoryLoading] = useState(true);
  const [historyItems, setHistoryItems] = useState<SpreadsheetImportBatchSummaryDto[]>([]);
  const [selectedBatch, setSelectedBatch] = useState<SpreadsheetImportBatchDetailDto | null>(null);
  const [batchLoadingId, setBatchLoadingId] = useState<number | null>(null);
  const [rollbackLoadingId, setRollbackLoadingId] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [historyError, setHistoryError] = useState<string | null>(null);
  const [databaseStatus, setDatabaseStatus] = useState<DatabaseStatusDto | null>(null);
  const [databaseStatusLoading, setDatabaseStatusLoading] = useState(false);
  const [sqliteFile, setSqliteFile] = useState<File | null>(null);
  const [sqliteImportLoading, setSqliteImportLoading] = useState(false);
  const [sqliteImportError, setSqliteImportError] = useState<string | null>(null);
  const [sqliteImportResult, setSqliteImportResult] = useState<DatabaseImportResultDto | null>(null);

  const activeSection = useMemo(
    () => preview?.sections.find((section) => section.key === activeSectionKey) ?? preview?.sections[0] ?? null,
    [activeSectionKey, preview],
  );

  const allPreviewRows = useMemo(
    () => preview?.sections.flatMap((section) => section.rows) ?? [],
    [preview],
  );

  const previewRowById = useMemo(
    () => new Map(allPreviewRows.map((row) => [row.previewId, row])),
    [allPreviewRows],
  );

  const selectableRows = useMemo(
    () => preview?.sections.flatMap((section) => section.rows.filter((row) => row.isSelectable)) ?? [],
    [preview],
  );

  const counterpartyRows = useMemo(
    () => preview?.sections.find((section) => section.key === 'counterparties')?.rows ?? [],
    [preview],
  );

  const requiredCounterpartiesByDocumentId = useMemo(() => {
    const map = new Map<string, SpreadsheetImportPreviewRowDto[]>();

    counterpartyRows.forEach((row) => {
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
  }, [counterpartyRows]);

  const dependentRequiredDocumentsByCounterpartyId = useMemo(() => {
    const map = new Map<string, SpreadsheetImportPreviewRowDto[]>();

    counterpartyRows.forEach((row) => {
      if (row.action !== 'Created' || row.dependencyPreviewIds.length === 0) {
        return;
      }

      const dependentRows = row.dependencyPreviewIds
        .map((previewId) => previewRowById.get(previewId))
        .filter((item): item is SpreadsheetImportPreviewRowDto => Boolean(item));

      map.set(row.previewId, dependentRows);
    });

    return map;
  }, [counterpartyRows, previewRowById]);

  const missingRequiredCounterparties = useMemo(() => {
    const missing = new Map<string, SpreadsheetImportPreviewRowDto>();

    requiredCounterpartiesByDocumentId.forEach((rows, previewId) => {
      if (!selectedPreviewIds.has(previewId)) {
        return;
      }

      rows.forEach((row) => {
        if (!selectedPreviewIds.has(row.previewId)) {
          missing.set(row.previewId, row);
        }
      });
    });

    return Array.from(missing.values());
  }, [requiredCounterpartiesByDocumentId, selectedPreviewIds]);

  const selectedCount = selectedPreviewIds.size;

  const loadBatch = useCallback(async (batchId: number) => {
    setBatchLoadingId(batchId);
    try {
      const batch = await apiService.getSpreadsheetImportBatch(batchId);
      setSelectedBatch(batch);
      setHistoryError(null);
    } catch (loadError) {
      const message = extractDisplayErrorMessage(loadError, 'Не удалось загрузить детали импорта.');
      setHistoryError(message);
      toast.error(message, 'Импорт данных');
    } finally {
      setBatchLoadingId(null);
    }
  }, [toast]);

  const loadHistory = useCallback(async (selectBatchId?: number) => {
    setHistoryLoading(true);
    try {
      const items = await apiService.getSpreadsheetImportBatches();
      setHistoryItems(items ?? []);
      setHistoryError(null);
      if (selectBatchId) {
        await loadBatch(selectBatchId);
      }
    } catch (loadError) {
      setHistoryError(extractDisplayErrorMessage(loadError, 'Не удалось загрузить историю импортов.'));
    } finally {
      setHistoryLoading(false);
    }
  }, [loadBatch]);

  const loadDatabaseStatus = useCallback(async () => {
    setDatabaseStatusLoading(true);
    try {
      const status = await apiService.getDatabaseStatus();
      setDatabaseStatus(status);
      setSqliteImportError(null);
    } catch (loadError) {
      const message = extractDisplayErrorMessage(loadError, 'Не удалось загрузить статус базы данных.');
      setSqliteImportError(message);
    } finally {
      setDatabaseStatusLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadHistory();
    void loadDatabaseStatus();
  }, [loadDatabaseStatus, loadHistory]);

  useEffect(() => {
    if (preview?.sections.length && !preview.sections.some((section) => section.key === activeSectionKey)) {
      setActiveSectionKey(preview.sections[0].key);
    }
  }, [activeSectionKey, preview]);

  useEffect(() => {
    setPreviewPage(1);
  }, [activeSectionKey, previewPageSize, preview]);

  useEffect(() => {
    if (!activeSection) {
      return;
    }

    const totalPages = Math.max(1, Math.ceil(activeSection.rows.length / previewPageSize || 1));
    setPreviewPage((previous) => Math.min(previous, totalPages));
  }, [activeSection, previewPageSize]);

  function addRowsToSelection(next: Set<string>, rows: SpreadsheetImportPreviewRowDto[]) {
    const queue = [...rows];
    const seen = new Set<string>();

    while (queue.length > 0) {
      const row = queue.shift();
      if (!row || seen.has(row.previewId)) {
        continue;
      }

      seen.add(row.previewId);
      if (!row.isSelectable) {
        continue;
      }

      next.add(row.previewId);
      (requiredCounterpartiesByDocumentId.get(row.previewId) ?? []).forEach((dependency) => {
        queue.push(dependency);
      });
    }
  }

  function pruneUnusedRequiredCounterparties(next: Set<string>) {
    let changed = true;

    while (changed) {
      changed = false;

      counterpartyRows.forEach((row) => {
        if (row.action !== 'Created' || row.dependencyPreviewIds.length === 0 || !next.has(row.previewId)) {
          return;
        }

        const hasSelectedDependency = row.dependencyPreviewIds.some((previewId) => next.has(previewId));
        if (!hasSelectedDependency) {
          next.delete(row.previewId);
          changed = true;
        }
      });
    }
  }

  function removeRowsFromSelection(next: Set<string>, rows: SpreadsheetImportPreviewRowDto[]) {
    const queue = [...rows];
    const seen = new Set<string>();

    while (queue.length > 0) {
      const row = queue.shift();
      if (!row || seen.has(row.previewId)) {
        continue;
      }

      seen.add(row.previewId);
      next.delete(row.previewId);

      (dependentRequiredDocumentsByCounterpartyId.get(row.previewId) ?? []).forEach((dependency) => {
        queue.push(dependency);
      });
    }

    pruneUnusedRequiredCounterparties(next);
  }

  function selectRows(rows: SpreadsheetImportPreviewRowDto[]) {
    setSelectedPreviewIds((previous) => {
      const next = new Set(previous);
      addRowsToSelection(next, rows);
      return next;
    });
  }

  function clearRows(rows: SpreadsheetImportPreviewRowDto[]) {
    setSelectedPreviewIds((previous) => {
      const next = new Set(previous);
      removeRowsFromSelection(next, rows);
      return next;
    });
  }

  async function analyzeFiles() {
    if (!files.length) {
      const message = 'Сначала выберите хотя бы один Excel-файл.';
      setError(message);
      toast.error(message, 'Импорт данных');
      return;
    }

    setPreviewLoading(true);
    setError(null);
    try {
      const result = await apiService.previewSpreadsheetImport(files);
      const initialCounterpartyImportModes = Object.fromEntries(
        result.sections
          .filter((section) => section.key === 'counterparties')
          .flatMap((section) =>
            section.rows
              .filter((row) => Boolean(row.counterpartyImportMode))
              .map((row) => [row.previewId, row.counterpartyImportMode as string]),
          ),
      );
      setPreview(result);
      setActiveSectionKey(result.sections[0]?.key ?? '');
      setPreviewPage(1);
      setSelectedPreviewIds(new Set(
        result.sections.flatMap((section) => section.rows.filter((row) => row.isSelectable).map((row) => row.previewId)),
      ));
      setCounterpartyImportModes(initialCounterpartyImportModes);
      setLastSelectedPreviewId(null);
      setPreviewModalOpen(true);
      toast.success('Файлы проанализированы, можно подтвердить импорт.', 'Импорт данных');
    } catch (loadError) {
      const message = extractDisplayErrorMessage(loadError, 'Не удалось проанализировать файлы импорта.');
      setError(message);
      toast.error(message, 'Импорт данных');
    } finally {
      setPreviewLoading(false);
    }
  }

  function toggleRow(row: SpreadsheetImportPreviewRowDto, checked: boolean, shiftKey: boolean) {
    if (!activeSection) return;

    setSelectedPreviewIds((previous) => {
      const next = new Set(previous);
      const rows = activeSection.rows;
      const targetIndex = rows.findIndex((item) => item.previewId === row.previewId);
      const lastIndex = lastSelectedPreviewId ? rows.findIndex((item) => item.previewId === lastSelectedPreviewId) : -1;
      if (shiftKey && targetIndex >= 0 && lastIndex >= 0) {
        const [from, to] = targetIndex < lastIndex ? [targetIndex, lastIndex] : [lastIndex, targetIndex];
        const affectedRows = rows.slice(from, to + 1).filter((item) => item.isSelectable);
        if (checked) {
          addRowsToSelection(next, affectedRows);
        } else {
          removeRowsFromSelection(next, affectedRows);
        }
      } else if (checked) {
        addRowsToSelection(next, [row]);
      } else {
        removeRowsFromSelection(next, [row]);
      }

      return next;
    });

    setLastSelectedPreviewId(row.previewId);
  }

  function selectAll(section?: SpreadsheetImportPreviewSectionDto) {
    selectRows(section ? section.rows : selectableRows);
  }

  function clearAll(section?: SpreadsheetImportPreviewSectionDto) {
    clearRows(section ? section.rows : selectableRows);
  }

  async function runImport() {
    if (!preview || !files.length || selectedCount === 0) {
      const message = 'Выберите хотя бы одну запись для импорта.';
      setError(message);
      toast.error(message, 'Импорт данных');
      return;
    }

    if (missingRequiredCounterparties.length > 0) {
      const titles = missingRequiredCounterparties.map((row) => row.title).join(', ');
      const message = `Для выбранных счетов или актов не выбраны обязательные контрагенты: ${titles}.`;
      setError(message);
      toast.error(message, 'Импорт данных');
      return;
    }

    const approved = await confirm({
      title: 'Запустить импорт',
      description: `Будет импортировано записей: ${selectedCount}. Система сохранит историю изменений и позволит откатить партию.`,
      confirmLabel: 'Импортировать',
      cancelLabel: 'Отмена',
    });
    if (!approved) return;

    setApplyLoading(true);
    setError(null);
    try {
      const batch = await apiService.applySpreadsheetImport(
        files,
        Array.from(selectedPreviewIds),
        counterpartyRows
          .map((row) => ({
            previewId: row.previewId,
            importMode: counterpartyImportModes[row.previewId] ?? row.counterpartyImportMode ?? '',
          }))
          .filter((option) => Boolean(option.importMode)),
      );
      setSelectedBatch(batch);
      await loadHistory(batch.id);
      setPreviewModalOpen(false);
      toast.success('Импорт завершен.', 'Импорт данных');
    } catch (loadError) {
      const message = extractDisplayErrorMessage(loadError, 'Не удалось выполнить импорт.');
      setError(message);
      toast.error(message, 'Импорт данных');
    } finally {
      setApplyLoading(false);
    }
  }

  async function rollbackBatch(batch: SpreadsheetImportBatchSummaryDto) {
    const approved = await confirm({
      title: 'Откатить импорт',
      description: `Будет выполнен откат партии "${batch.title}". Откат пропустит записи, которые были изменены после импорта.`,
      confirmLabel: 'Откатить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    setRollbackLoadingId(batch.id);
    try {
      const result = await apiService.rollbackSpreadsheetImportBatch(batch.id);
      setSelectedBatch(result);
      await loadHistory(result.id);
      toast.success('Откат партии завершен.', 'Импорт данных');
    } catch (loadError) {
      const message = extractDisplayErrorMessage(loadError, 'Не удалось откатить импорт.');
      setHistoryError(message);
      toast.error(message, 'Импорт данных');
    } finally {
      setRollbackLoadingId(null);
    }
  }

  async function runSqliteImport() {
    if (!sqliteFile) {
      const message = 'Выберите SQLite-файл для импорта.';
      setSqliteImportError(message);
      toast.error(message, 'Импорт данных');
      return;
    }

    const approved = await confirm({
      title: 'Импортировать SQLite-файл',
      description: 'Будет выполнен перенос данных из выбранного SQLite-файла в текущую базу.',
      confirmLabel: 'Импортировать',
      cancelLabel: 'Отмена',
    });
    if (!approved) return;

    setSqliteImportLoading(true);
    setSqliteImportError(null);
    try {
      const result = await apiService.importSqliteDatabase(sqliteFile);
      setSqliteImportResult(result);
      toast.success('Импорт SQLite-файла завершен.', 'Импорт данных');
    } catch (loadError) {
      const message = extractDisplayErrorMessage(loadError, 'Не удалось выполнить импорт SQLite-файла.');
      setSqliteImportError(message);
      toast.error(message, 'Импорт данных');
    } finally {
      setSqliteImportLoading(false);
    }
  }

  return (
    <div className="space-y-6">
      <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
        <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
          <div className="space-y-2">
            <div className="inline-flex items-center gap-2 rounded-full border border-emerald-200 bg-emerald-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.18em] text-emerald-700">
              <FileSpreadsheet className="h-3.5 w-3.5" />
              Импорт табличных данных
            </div>
            <h4 className="text-xl font-semibold text-gray-900">Excel, архивы и отчетные выгрузки</h4>
            <p className="max-w-3xl text-sm text-gray-600">
              Система анализирует загруженные файлы, сама определяет тип данных, показывает отдельные секции для счетов,
              актов и контрагентов, а затем сохраняет историю партии с возможностью безопасного отката.
            </p>
          </div>
          <button
            type="button"
            onClick={() => void loadHistory(selectedBatch?.id)}
            disabled={historyLoading}
            className="inline-flex items-center gap-2 rounded-xl border border-gray-300 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {historyLoading ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
            Обновить историю
          </button>
        </div>

        <div className="mt-5 grid gap-4 lg:grid-cols-[minmax(0,1fr)_260px]">
          <label className="rounded-2xl border border-dashed border-gray-300 bg-slate-50/80 p-5 text-sm text-gray-700">
            <span className="mb-2 block font-medium text-gray-900">Файлы для анализа</span>
            <input
              type="file"
              multiple
              accept=".xls,.xlsx,.xlsm,.xlsb"
              onChange={(event) => {
                setFiles(Array.from(event.target.files ?? []));
                setPreview(null);
                setPreviewModalOpen(false);
                setError(null);
                setSelectedPreviewIds(new Set());
                setCounterpartyImportModes({});
                setLastSelectedPreviewId(null);
                setPreviewPage(1);
              }}
              className="block w-full text-sm text-slate-500 file:mr-3 file:rounded-lg file:border-0 file:bg-blue-50 file:px-3 file:py-2 file:text-blue-700"
            />
            <div className="mt-3 space-y-1 text-xs text-gray-500">
              {files.length > 0 ? files.map((file) => <div key={file.name}>{file.name}</div>) : <div>Файлы пока не выбраны.</div>}
            </div>
          </label>

          <div className="rounded-2xl border border-gray-200 p-5">
            <div className="text-xs font-semibold uppercase tracking-[0.18em] text-gray-500">Подтверждение</div>
            <div className="mt-3 text-sm text-gray-700">
              <div>{formatCount('Файлов', files.length)}</div>
              <div>{formatCount('Доступно строк', preview?.totalSelectableRows ?? 0)}</div>
              <div>{formatCount('Выбрано', selectedCount)}</div>
            </div>
            <div className="mt-4 space-y-2">
              <button
                type="button"
                onClick={analyzeFiles}
                disabled={previewLoading || applyLoading || files.length === 0}
                className="inline-flex w-full items-center justify-center gap-2 rounded-xl bg-slate-900 px-4 py-2.5 text-sm font-medium text-white hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-60"
              >
                {previewLoading ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
                Анализировать
              </button>
              <button
                type="button"
                onClick={() => setPreviewModalOpen(true)}
                disabled={!preview || previewLoading}
                className="inline-flex w-full items-center justify-center gap-2 rounded-xl border border-emerald-200 bg-emerald-50 px-4 py-2.5 text-sm font-medium text-emerald-800 hover:bg-emerald-100 disabled:cursor-not-allowed disabled:opacity-60"
              >
                <CheckSquare className="h-4 w-4" />
                Открыть предпросмотр
              </button>
            </div>
          </div>
        </div>

        {error && (
          <div className="mt-4 rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
            {error}
          </div>
        )}
      </div>

      {preview && (
        <div className="rounded-3xl border border-emerald-200 bg-emerald-50/60 p-6 shadow-sm">
          <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
            <div className="space-y-2">
              <div className="inline-flex items-center gap-2 rounded-full border border-emerald-200 bg-white px-3 py-1 text-xs font-semibold uppercase tracking-[0.18em] text-emerald-700">
                <FileSpreadsheet className="h-3.5 w-3.5" />
                Анализ завершен
              </div>
              <h4 className="text-lg font-semibold text-gray-900">Предпросмотр вынесен в отдельное окно подтверждения</h4>
              <p className="max-w-3xl text-sm text-gray-700">
                В окне предпросмотра доступны вкладки по разделам, пагинация, массовое выделение по секции и странице,
                а также сравнение полей перед созданием или обновлением записей.
              </p>
            </div>
            <button
              type="button"
              onClick={() => setPreviewModalOpen(true)}
              className="inline-flex items-center gap-2 rounded-xl bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-800"
            >
              <CheckSquare className="h-4 w-4" />
              Открыть окно предпросмотра
            </button>
          </div>

          <div className="mt-4 flex flex-wrap gap-3 text-sm text-gray-700">
            <span>{formatCount('Файлов', preview.files.length)}</span>
            <span>{formatCount('Секций', preview.sections.length)}</span>
            <span>{formatCount('Доступно строк', preview.totalSelectableRows)}</span>
            <span>{formatCount('Выбрано', selectedCount)}</span>
          </div>
        </div>
      )}

      <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
        <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
          <div className="space-y-2">
            <div className="inline-flex items-center gap-2 rounded-full border border-slate-200 bg-slate-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.18em] text-slate-600">
              SQLite Legacy
            </div>
            <h4 className="text-lg font-semibold text-gray-900">Импорт старой SQLite-базы</h4>
            <p className="max-w-3xl text-sm text-gray-600">
              Старый сценарий импорта сохранен отдельно: можно загрузить существующий SQLite-файл и перенести данные в текущую базу.
            </p>
          </div>
          <button
            type="button"
            onClick={() => void loadDatabaseStatus()}
            disabled={databaseStatusLoading || sqliteImportLoading}
            className="inline-flex items-center gap-2 rounded-xl border border-gray-300 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {databaseStatusLoading ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
            Обновить статус
          </button>
        </div>

        <div className="mt-5 grid gap-4 lg:grid-cols-[minmax(0,1fr)_320px]">
          <label className="rounded-2xl border border-dashed border-gray-300 bg-slate-50/80 p-5 text-sm text-gray-700">
            <span className="mb-2 block font-medium text-gray-900">SQLite-файл</span>
            <input
              type="file"
              accept=".db,.sqlite,.sqlite3,application/octet-stream"
              onChange={(event) => {
                setSqliteFile(event.target.files?.[0] ?? null);
                setSqliteImportError(null);
                setSqliteImportResult(null);
              }}
              className="block w-full text-sm text-slate-500 file:mr-3 file:rounded-lg file:border-0 file:bg-blue-50 file:px-3 file:py-2 file:text-blue-700"
            />
            <div className="mt-3 text-xs text-gray-500">{sqliteFile?.name ?? 'Файл пока не выбран.'}</div>
          </label>

          <div className="rounded-2xl border border-gray-200 p-5">
            <div className="text-xs font-semibold uppercase tracking-[0.18em] text-gray-500">Текущая база</div>
            <div className="mt-3 space-y-1 text-sm text-gray-700">
              <div>{databaseStatus?.displayName ?? '—'}</div>
              <div>{databaseStatus?.supportsSqliteFileImport === false ? 'Импорт SQLite недоступен' : 'Импорт SQLite доступен'}</div>
            </div>
            <button
              type="button"
              onClick={runSqliteImport}
              disabled={sqliteImportLoading || !sqliteFile || databaseStatus?.supportsSqliteFileImport === false}
              className="mt-4 inline-flex w-full items-center justify-center gap-2 rounded-xl bg-slate-900 px-4 py-2.5 text-sm font-medium text-white hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-60"
            >
              {sqliteImportLoading ? <Loader2 className="h-4 w-4 animate-spin" /> : <CheckSquare className="h-4 w-4" />}
              Импортировать SQLite
            </button>
          </div>
        </div>

        {sqliteImportError && (
          <div className="mt-4 rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
            {sqliteImportError}
          </div>
        )}

        {sqliteImportResult && (
          <div className="mt-4 rounded-2xl border border-slate-200 bg-slate-50 p-4">
            <div className="flex flex-wrap gap-3 text-sm text-slate-700">
              <span>{formatCount('Таблиц обработано', sqliteImportResult.sourceTablesProcessed)}</span>
              <span>{formatCount('Таблиц с ошибками', sqliteImportResult.tablesWithErrors)}</span>
              <span>{sqliteImportResult.success ? 'Импорт завершен успешно' : 'Импорт завершен с ошибками'}</span>
            </div>
            <div className="mt-4 space-y-2">
              {sqliteImportResult.tables.map((table) => (
                <div key={table.table} className="rounded-xl border border-white bg-white px-4 py-3 text-sm text-slate-700 shadow-sm">
                  <div className="font-medium text-slate-900">{table.displayName}</div>
                  <div className="mt-1 flex flex-wrap gap-3 text-xs text-slate-600">
                    <span>{formatCount('Источник', table.sourceRows)}</span>
                    <span>{formatCount('Создано', table.inserted)}</span>
                    <span>{formatCount('Обновлено', table.updated)}</span>
                    <span>{formatCount('Пропущено', table.skipped)}</span>
                    <span>{formatCount('Конфликты', table.conflicts)}</span>
                    <span>{formatCount('Ошибки', table.errors)}</span>
                  </div>
                  {table.message && <div className="mt-2 text-xs text-slate-500">{table.message}</div>}
                </div>
              ))}
            </div>
          </div>
        )}
      </div>

      <div className="grid gap-6 xl:grid-cols-[360px_minmax(0,1fr)]">
        <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
          <div className="flex items-center gap-2 text-lg font-semibold text-gray-900">
            <History className="h-5 w-5 text-slate-500" />
            История партий
          </div>
          {historyError && <div className="mt-4 rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{historyError}</div>}
          <div className="mt-4 space-y-3">
            {historyLoading ? (
              <div className="inline-flex items-center gap-2 text-sm text-gray-600">
                <Loader2 className="h-4 w-4 animate-spin" />
                Загрузка истории…
              </div>
            ) : historyItems.length === 0 ? (
              <div className="text-sm text-gray-600">История импортов пока пуста.</div>
            ) : (
              historyItems.map((batch) => (
                <div key={batch.id} className={`rounded-2xl border p-4 ${selectedBatch?.id === batch.id ? 'border-emerald-200 bg-emerald-50/40' : 'border-gray-200 bg-white'}`}>
                  <div className="flex items-start justify-between gap-3">
                    <div>
                      <div className="font-semibold text-gray-900">{batch.title}</div>
                      <div className="mt-1 text-xs text-gray-500">{formatDateTime(batch.createdAt)}</div>
                    </div>
                    <span className={`inline-flex rounded-full border px-2.5 py-1 text-xs font-semibold ${badgeClass(batch.status)}`}>
                      {formatBatchStatusLabel(batch.status)}
                    </span>
                  </div>
                  <div className="mt-3 text-sm text-gray-600">
                    <div>{formatCount('Строк', batch.totalRows)}</div>
                    <div>{formatCount('Создано', batch.createdRows)}</div>
                    <div>{formatCount('Обновлено', batch.updatedRows)}</div>
                    <div>{formatCount('Ошибок', batch.errorRows)}</div>
                  </div>
                  <div className="mt-3 flex gap-2">
                    <button
                      type="button"
                      onClick={() => void loadBatch(batch.id)}
                      disabled={batchLoadingId === batch.id}
                      className="inline-flex items-center gap-2 rounded-lg border border-gray-300 px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
                    >
                      {batchLoadingId === batch.id ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
                      Детали
                    </button>
                    {!batch.rolledBackAt && (
                      <button
                        type="button"
                        onClick={() => void rollbackBatch(batch)}
                        disabled={rollbackLoadingId === batch.id}
                        className="inline-flex items-center gap-2 rounded-lg border border-red-300 px-3 py-2 text-sm font-medium text-red-700 hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-60"
                      >
                        {rollbackLoadingId === batch.id ? <Loader2 className="h-4 w-4 animate-spin" /> : <RotateCcw className="h-4 w-4" />}
                        Откатить
                      </button>
                    )}
                  </div>
                </div>
              ))
            )}
          </div>
        </div>

        <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
          <h4 className="text-lg font-semibold text-gray-900">Детали партии</h4>
          {!selectedBatch ? (
            <div className="mt-4 text-sm text-gray-600">Выберите партию слева, чтобы посмотреть подробный журнал действий.</div>
          ) : (
            <div className="mt-4 space-y-4">
              <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
                <div className="rounded-2xl border border-gray-200 p-4 text-sm text-gray-700">{formatCount('Всего строк', selectedBatch.totalRows)}</div>
                <div className="rounded-2xl border border-gray-200 p-4 text-sm text-gray-700">{formatCount('Создано', selectedBatch.createdRows)}</div>
                <div className="rounded-2xl border border-gray-200 p-4 text-sm text-gray-700">{formatCount('Обновлено', selectedBatch.updatedRows)}</div>
                <div className="rounded-2xl border border-gray-200 p-4 text-sm text-gray-700">{formatCount('Откат', selectedBatch.rollbackRows)}</div>
              </div>

              <div className="space-y-3">
                {selectedBatch.operations.map((operation) => (
                  <div key={operation.id} className="rounded-2xl border border-gray-200 p-4">
                    <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
                      <div>
                        <div className="flex flex-wrap items-center gap-2">
                          <span className="font-semibold text-gray-900">{operation.sourceTitle}</span>
                          <span className={`inline-flex rounded-full border px-2.5 py-1 text-xs font-semibold ${badgeClass(operation.action)}`}>
                            {formatOperationActionLabel(operation.action)}
                          </span>
                          {operation.rollbackAction && (
                            <span className={`inline-flex rounded-full border px-2.5 py-1 text-xs font-semibold ${badgeClass(operation.rollbackAction)}`}>
                              {formatRollbackActionLabel(operation.rollbackAction)}
                            </span>
                          )}
                        </div>
                        <div className="mt-1 text-sm text-gray-600">
                          {formatSectionLabel(operation.sectionKey)} · {operation.sourceFileName} · строка {operation.sourceRowNumber}
                        </div>
                        {operation.message && <div className="mt-2 text-sm text-gray-700">{operation.message}</div>}
                        {operation.rollbackMessage && <div className="mt-1 text-sm text-amber-700">{operation.rollbackMessage}</div>}
                      </div>
                      <div className="text-xs text-gray-500">
                        <div>{formatDateTime(operation.createdAt)}</div>
                        {operation.rolledBackAt && <div>Откат: {formatDateTime(operation.rolledBackAt)}</div>}
                      </div>
                    </div>
                  </div>
                ))}
              </div>
            </div>
          )}
        </div>
      </div>

      <SpreadsheetImportPreviewModal
        open={previewModalOpen}
        preview={preview}
        activeSectionKey={activeSectionKey}
        selectedPreviewIds={selectedPreviewIds}
        counterpartyImportModes={counterpartyImportModes}
        applyLoading={applyLoading}
        error={error}
        missingRequiredCounterparties={missingRequiredCounterparties}
        page={previewPage}
        pageSize={previewPageSize}
        pageSizeOptions={PREVIEW_PAGE_SIZE_OPTIONS}
        onClose={() => setPreviewModalOpen(false)}
        onSectionChange={setActiveSectionKey}
        onPageChange={setPreviewPage}
        onPageSizeChange={setPreviewPageSize}
        onToggleRow={toggleRow}
        onSelectAll={() => selectAll()}
        onClearAll={() => clearAll()}
        onSelectSection={selectAll}
        onClearSection={clearAll}
        onSelectRows={selectRows}
        onClearRows={clearRows}
        onCounterpartyImportModeChange={(previewId, importMode) =>
          setCounterpartyImportModes((previous) => ({
            ...previous,
            [previewId]: importMode,
          }))
        }
        onRunImport={() => void runImport()}
      />
    </div>
  );
}
