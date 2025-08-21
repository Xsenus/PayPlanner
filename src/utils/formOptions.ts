export function buildOptionsWithSelected<
  T extends { id: number; name: string; isActive?: boolean },
>(list: T[], selectedId?: number, activePred?: (x: T) => boolean): T[] {
  const active = activePred ? list.filter(activePred) : list.slice();

  const hasSelected = (id?: number) => id != null && active.some((x) => x.id === id);

  if (!hasSelected(selectedId) && selectedId != null) {
    const sel = list.find((x) => x.id === selectedId);
    if (sel) active.push(sel);
  }

  return active.sort((a, b) => {
    const ai = a.isActive ?? true;
    const bi = b.isActive ?? true;
    if (ai !== bi) return ai ? -1 : 1;
    return a.name.localeCompare(b.name, 'ru', { sensitivity: 'base' });
  });
}
