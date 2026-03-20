import { useCallback, useEffect, useMemo, useState } from 'react';

type SelectionId = number | string;

function uniqueIds<T extends SelectionId>(ids: readonly T[]): T[] {
  return Array.from(new Set(ids));
}

export function useTableSelection<T extends SelectionId>(visibleIds: readonly T[]) {
  const [selectedIds, setSelectedIds] = useState<T[]>([]);

  const visibleIdSet = useMemo(() => new Set(visibleIds), [visibleIds]);
  const selectedIdSet = useMemo(() => new Set(selectedIds), [selectedIds]);

  useEffect(() => {
    setSelectedIds((prev) => prev.filter((id) => visibleIdSet.has(id)));
  }, [visibleIdSet]);

  const selectedVisibleCount = useMemo(
    () => visibleIds.reduce((count, id) => count + (selectedIdSet.has(id) ? 1 : 0), 0),
    [selectedIdSet, visibleIds],
  );

  const allVisibleSelected = visibleIds.length > 0 && selectedVisibleCount === visibleIds.length;
  const someVisibleSelected = selectedVisibleCount > 0 && !allVisibleSelected;

  const isSelected = useCallback((id: T) => selectedIdSet.has(id), [selectedIdSet]);

  const replaceSelection = useCallback((ids: readonly T[]) => {
    setSelectedIds(uniqueIds(ids));
  }, []);

  const clearSelection = useCallback(() => {
    setSelectedIds([]);
  }, []);

  const selectOnly = useCallback((id: T) => {
    setSelectedIds([id]);
  }, []);

  const toggleSelection = useCallback((id: T, nextValue?: boolean) => {
    setSelectedIds((prev) => {
      const hasId = prev.includes(id);
      const shouldSelect = nextValue ?? !hasId;

      if (shouldSelect) {
        return hasId ? prev : [...prev, id];
      }

      return hasId ? prev.filter((value) => value !== id) : prev;
    });
  }, []);

  const toggleAllVisible = useCallback((nextValue?: boolean) => {
    setSelectedIds((prev) => {
      const shouldSelect = nextValue ?? !allVisibleSelected;
      const preservedIds = prev.filter((id) => !visibleIdSet.has(id));

      if (!shouldSelect) {
        return preservedIds;
      }

      return uniqueIds([...preservedIds, ...visibleIds]);
    });
  }, [allVisibleSelected, visibleIdSet, visibleIds]);

  return {
    selectedIds,
    selectedIdSet,
    selectedCount: selectedIds.length,
    selectedVisibleCount,
    allVisibleSelected,
    someVisibleSelected,
    isSelected,
    replaceSelection,
    clearSelection,
    selectOnly,
    toggleSelection,
    toggleAllVisible,
  };
}
