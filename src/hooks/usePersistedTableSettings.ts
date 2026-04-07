import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type DragEvent as ReactDragEvent,
  type MouseEvent as ReactMouseEvent,
} from 'react';
import { apiService } from '../services/api';

type TableSettingsRecord<K extends string> = {
  visibleColumns: K[];
  columnOrder: K[];
  columnWidths: Partial<Record<K, number>>;
  pageSize: number;
};

type LegacyTableSettingsRecord<K extends string> = {
  visible?: Partial<Record<K, boolean>>;
  order?: K[];
  widths?: Partial<Record<K, number>>;
  pageSize?: number;
};

type TableColumnConfig<K extends string> = {
  key: K;
  defaultVisible?: boolean;
  defaultWidth?: number;
};

type UsePersistedTableSettingsOptions<K extends string> = {
  storageKey: string;
  settingsKey: string;
  columns: TableColumnConfig<K>[];
  defaultPageSize: number;
  minPageSize?: number;
  maxPageSize?: number;
  minColumnWidth?: number;
};

function safeRead<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(key);
    if (!raw) return fallback;
    const parsed = JSON.parse(raw) as T;
    return parsed ?? fallback;
  } catch {
    return fallback;
  }
}

function safeWrite<T>(key: string, value: T) {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    /** */
  }
}

function getColumnsSignature<K extends string>(columns: TableColumnConfig<K>[]): string {
  return columns
    .map((column) => `${column.key}:${column.defaultVisible === false ? '0' : '1'}:${column.defaultWidth ?? ''}`)
    .join('|');
}

export function usePersistedTableSettings<K extends string>({
  storageKey,
  settingsKey,
  columns,
  defaultPageSize,
  minPageSize = 5,
  maxPageSize = 100,
  minColumnWidth = 96,
}: UsePersistedTableSettingsOptions<K>) {
  const columnsSignature = getColumnsSignature(columns);
  const stableColumnsRef = useRef<{ signature: string; columns: TableColumnConfig<K>[] } | null>(null);

  if (!stableColumnsRef.current || stableColumnsRef.current.signature !== columnsSignature) {
    stableColumnsRef.current = {
      signature: columnsSignature,
      columns: columns.map(({ key, defaultVisible, defaultWidth }) => ({ key, defaultVisible, defaultWidth })),
    };
  }

  const stableColumns = stableColumnsRef.current.columns;
  const allKeys = useMemo(() => stableColumns.map((column) => column.key), [stableColumns]);
  const defaultVisibleColumns = useMemo(
    () => stableColumns.filter((column) => column.defaultVisible !== false).map((column) => column.key),
    [stableColumns],
  );
  const defaultColumnWidths = useMemo(
    () =>
      stableColumns.reduce<Partial<Record<K, number>>>((acc, column) => {
        if (typeof column.defaultWidth === 'number') {
          acc[column.key] = Math.max(minColumnWidth, column.defaultWidth);
        }
        return acc;
      }, {}),
    [stableColumns, minColumnWidth],
  );

  const normalizeSettings = useMemo(
    () =>
      (parsed?: Partial<TableSettingsRecord<K>> | null): TableSettingsRecord<K> => {
        const legacyParsed = (parsed ?? null) as (Partial<TableSettingsRecord<K>> & LegacyTableSettingsRecord<K>) | null;
        const legacyVisibleColumns = Object.entries(legacyParsed?.visible ?? {}).reduce<K[]>(
          (acc, [key, value]) => {
            if (value !== true) return acc;
            if (!allKeys.includes(key as K)) return acc;
            acc.push(key as K);
            return acc;
          },
          [],
        );

        const visibleColumns = Array.isArray(legacyParsed?.visibleColumns)
          ? legacyParsed.visibleColumns.filter((value): value is K => allKeys.includes(value as K))
          : legacyVisibleColumns;
        const rawOrder = Array.isArray(legacyParsed?.columnOrder)
          ? legacyParsed.columnOrder.filter((value): value is K => allKeys.includes(value as K))
          : Array.isArray(legacyParsed?.order)
            ? legacyParsed.order.filter((value): value is K => allKeys.includes(value as K))
            : [];
        const columnOrder = rawOrder.length
          ? [...rawOrder, ...allKeys.filter((value) => !rawOrder.includes(value))]
          : allKeys;
        const columnWidths = Object.entries(legacyParsed?.columnWidths ?? legacyParsed?.widths ?? {}).reduce<Partial<Record<K, number>>>(
          (acc, [key, value]) => {
            if (!allKeys.includes(key as K)) return acc;
            if (typeof value !== 'number' || Number.isNaN(value)) return acc;
            acc[key as K] = Math.max(minColumnWidth, value);
            return acc;
          },
          { ...defaultColumnWidths },
        );

        return {
          visibleColumns: visibleColumns.length ? visibleColumns : defaultVisibleColumns,
          columnOrder,
          columnWidths,
          pageSize:
            typeof legacyParsed?.pageSize === 'number'
              ? Math.max(minPageSize, Math.min(maxPageSize, legacyParsed.pageSize))
              : defaultPageSize,
        };
      },
    [
      allKeys,
      defaultColumnWidths,
      defaultPageSize,
      defaultVisibleColumns,
      maxPageSize,
      minColumnWidth,
      minPageSize,
    ],
  );

  const initialSettings = useMemo(
    () => normalizeSettings(safeRead<Partial<TableSettingsRecord<K>> | null>(storageKey, null)),
    [normalizeSettings, storageKey],
  );

  const [visibleColumns, setVisibleColumns] = useState<K[]>(initialSettings.visibleColumns);
  const [columnOrder, setColumnOrder] = useState<K[]>(initialSettings.columnOrder);
  const [columnWidths, setColumnWidths] = useState<Partial<Record<K, number>>>(initialSettings.columnWidths);
  const [pageSize, setPageSize] = useState<number>(initialSettings.pageSize);
  const [settingsLoaded, setSettingsLoaded] = useState(false);
  const [draggingColumn, setDraggingColumn] = useState<K | null>(null);

  const resizingRef = useRef<{
    columnKey: K;
    startX: number;
    startWidth: number;
  } | null>(null);
  const saveTimerRef = useRef<number | null>(null);

  useEffect(() => {
    let active = true;

    const loadServerSettings = async () => {
      try {
        const response = await apiService.getUserSetting(settingsKey);
        if (!active || !response?.value) return;
        const parsed = JSON.parse(response.value) as Partial<TableSettingsRecord<K>>;
        const normalized = normalizeSettings(parsed);
        setVisibleColumns(normalized.visibleColumns);
        setColumnOrder(normalized.columnOrder);
        setColumnWidths(normalized.columnWidths);
        setPageSize(normalized.pageSize);
        safeWrite(storageKey, normalized);
      } catch {
        /** */
      } finally {
        if (active) {
          setSettingsLoaded(true);
        }
      }
    };

    void loadServerSettings();

    return () => {
      active = false;
    };
  }, [normalizeSettings, settingsKey, storageKey]);

  useEffect(() => {
    const payload = normalizeSettings({
      visibleColumns,
      columnOrder,
      columnWidths,
      pageSize,
    });
    safeWrite(storageKey, payload);
    if (!settingsLoaded) return;

    if (saveTimerRef.current) {
      window.clearTimeout(saveTimerRef.current);
    }

    saveTimerRef.current = window.setTimeout(() => {
      void apiService.upsertUserSetting(settingsKey, JSON.stringify(payload));
    }, 600);

    return () => {
      if (saveTimerRef.current) {
        window.clearTimeout(saveTimerRef.current);
      }
    };
  }, [
    columnOrder,
    columnWidths,
    normalizeSettings,
    pageSize,
    settingsKey,
    settingsLoaded,
    storageKey,
    visibleColumns,
  ]);

  const toggleColumn = (key: K) => {
    setVisibleColumns((prev) => {
      if (prev.includes(key)) {
        const next = prev.filter((value) => value !== key);
        return next.length ? next : prev;
      }

      const next = [...prev, key];
      next.sort((a, b) => columnOrder.indexOf(a) - columnOrder.indexOf(b));
      return next;
    });
  };

  const moveColumn = (columnKey: K, direction: 'up' | 'down') => {
    setColumnOrder((prev) => {
      const index = prev.indexOf(columnKey);
      if (index < 0) return prev;
      const target = direction === 'up' ? index - 1 : index + 1;
      if (target < 0 || target >= prev.length) return prev;
      const next = [...prev];
      [next[index], next[target]] = [next[target], next[index]];
      return next;
    });
  };

  const reorderColumns = (sourceKey: K, targetKey: K) => {
    if (sourceKey === targetKey) return;
    setColumnOrder((prev) => {
      const fromIndex = prev.indexOf(sourceKey);
      const toIndex = prev.indexOf(targetKey);
      if (fromIndex < 0 || toIndex < 0) return prev;
      const next = [...prev];
      next.splice(fromIndex, 1);
      next.splice(toIndex, 0, sourceKey);
      return next;
    });
  };

  const handleColumnDragStart = (columnKey: K, event: ReactDragEvent<HTMLElement>) => {
    event.dataTransfer.effectAllowed = 'move';
    event.dataTransfer.setData('text/plain', columnKey);
    setDraggingColumn(columnKey);
  };

  const handleColumnDragOver = (event: ReactDragEvent<HTMLElement>) => {
    event.preventDefault();
    event.dataTransfer.dropEffect = 'move';
  };

  const handleColumnDrop = (targetKey: K, event: ReactDragEvent<HTMLElement>) => {
    event.preventDefault();
    const sourceKey = event.dataTransfer.getData('text/plain') as K;
    if (!sourceKey) return;
    reorderColumns(sourceKey, targetKey);
    setDraggingColumn(null);
  };

  const handleColumnDragEnd = () => {
    setDraggingColumn(null);
  };

  const handleResizeStart = (columnKey: K, event: ReactMouseEvent<HTMLDivElement>) => {
    event.preventDefault();
    event.stopPropagation();
    const previousCursor = document.body.style.cursor;
    const previousUserSelect = document.body.style.userSelect;
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';

    resizingRef.current = {
      columnKey,
      startX: event.clientX,
      startWidth: columnWidths[columnKey] ?? defaultColumnWidths[columnKey] ?? minColumnWidth,
    };

    const handleMouseMove = (moveEvent: MouseEvent) => {
      if (!resizingRef.current) return;
      const delta = moveEvent.clientX - resizingRef.current.startX;
      const nextWidth = Math.max(minColumnWidth, resizingRef.current.startWidth + delta);
      setColumnWidths((prev) => ({
        ...prev,
        [resizingRef.current?.columnKey ?? columnKey]: nextWidth,
      }));
    };

    const handleMouseUp = () => {
      resizingRef.current = null;
      document.body.style.cursor = previousCursor;
      document.body.style.userSelect = previousUserSelect;
      window.removeEventListener('mousemove', handleMouseMove);
      window.removeEventListener('mouseup', handleMouseUp);
    };

    window.addEventListener('mousemove', handleMouseMove);
    window.addEventListener('mouseup', handleMouseUp);
  };

  const orderedVisibleColumns = useMemo(
    () => columnOrder.filter((key) => visibleColumns.includes(key)),
    [columnOrder, visibleColumns],
  );

  const getColumnWidth = (key: K) => {
    return columnWidths[key] ?? defaultColumnWidths[key] ?? minColumnWidth;
  };

  const resetTableSettings = () => {
    const normalized = normalizeSettings(null);
    setVisibleColumns(normalized.visibleColumns);
    setColumnOrder(normalized.columnOrder);
    setColumnWidths(normalized.columnWidths);
    setPageSize(normalized.pageSize);
  };

  return {
    visibleColumns,
    setVisibleColumns,
    columnOrder,
    setColumnOrder,
    columnWidths,
    setColumnWidths,
    orderedVisibleColumns,
    pageSize,
    setPageSize,
    settingsLoaded,
    draggingColumn,
    toggleColumn,
    moveColumn,
    reorderColumns,
    handleColumnDragStart,
    handleColumnDragOver,
    handleColumnDrop,
    handleColumnDragEnd,
    handleResizeStart,
    getColumnWidth,
    resetTableSettings,
  };
}
