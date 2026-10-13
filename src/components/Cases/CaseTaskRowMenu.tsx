import { useEffect, useId, useRef, useState } from 'react';
import { MoreHorizontal } from 'lucide-react';
import { buildCaseTaskCsv, downloadCaseTaskCsv } from '../../utils/caseTaskCsv';
import type { TaskJournalItem } from '../../utils/taskJournalStore';
import { TableContextMenu, type TableContextMenuAction, type TableContextMenuPosition } from '../ui/TableContextMenu';

export function CaseTaskRowMenu({ task, caseId, disabled, canSelect, selected, onToggle, canExport, statusLabels, onOpenTask }: {
  task: TaskJournalItem;
  caseId: number;
  disabled: boolean;
  canSelect: boolean;
  selected: boolean;
  onToggle: () => void;
  canExport: boolean;
  statusLabels: Record<string, string>;
  onOpenTask?: (id: number) => void;
}) {
  const buttonRef = useRef<HTMLButtonElement>(null);
  const menuId = useId();
  const [position, setPosition] = useState<TableContextMenuPosition | null>(null);
  const [initialFocus, setInitialFocus] = useState<'first' | 'last'>('first');
  const canOpen = Boolean(onOpenTask);
  const menuOpen = Boolean(position);
  const actions: TableContextMenuAction[] = [];
  if (onOpenTask) actions.push({ key: 'open', label: 'Открыть карточку', onSelect: () => { if (!disabled) onOpenTask(task.id); } });
  if (canSelect) actions.push({ key: 'select', label: selected ? 'Снять выбор задачи' : 'Выбрать задачу', onSelect: () => { if (!disabled) onToggle(); } });
  if (canExport) actions.push({ key: 'export', label: 'Экспорт задачи CSV', onSelect: () => {
    if (!disabled && canExport) downloadCaseTaskCsv(`case-${caseId}-task-${task.id}.csv`, buildCaseTaskCsv([task], statusLabels));
  } });

  useEffect(() => { setPosition(null); }, [task, disabled, canSelect, canExport, canOpen]);
  useEffect(() => {
    if (!menuOpen) return;
    const close = () => setPosition(null);
    const followAnchor = () => {
      const bounds = buttonRef.current?.getBoundingClientRect();
      if (!bounds || bounds.bottom <= 0 || bounds.top >= window.innerHeight
        || bounds.right <= 0 || bounds.left >= window.innerWidth) { close(); return; }
      const next = { x: Math.max(12, bounds.right - 220), y: bounds.bottom + 4 };
      setPosition((previous) => !previous || (previous.x === next.x && previous.y === next.y) ? previous : next);
    };
    window.addEventListener('resize', close);
    window.addEventListener('scroll', followAnchor, true);
    return () => { window.removeEventListener('resize', close); window.removeEventListener('scroll', followAnchor, true); };
  }, [menuOpen]);

  if (actions.length === 0) return null;
  const open = (focus: 'first' | 'last') => {
    if (disabled || !buttonRef.current) return;
    const bounds = buttonRef.current.getBoundingClientRect();
    setInitialFocus(focus);
    setPosition({ x: Math.max(12, bounds.right - 220), y: bounds.bottom + 4 });
  };

  return <>
    <button ref={buttonRef} type="button" aria-label={`Действия задачи: ${task.title}`} aria-haspopup="menu"
      aria-expanded={Boolean(position)} aria-controls={position ? menuId : undefined} disabled={disabled}
      className="inline-flex h-8 w-8 items-center justify-center rounded-lg text-blue-700 hover:bg-blue-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 disabled:opacity-50"
      onClick={() => position ? setPosition(null) : open('first')}
      onKeyDown={(event) => { if (event.key === 'ArrowDown' || event.key === 'ArrowUp') { event.preventDefault(); open(event.key === 'ArrowUp' ? 'last' : 'first'); } }}>
      <MoreHorizontal className="h-4 w-4" aria-hidden="true" />
    </button>
    <TableContextMenu open={Boolean(position) && !disabled} position={position} title="Действия задачи"
      actions={actions} onClose={() => setPosition(null)} keyboardNavigation initialFocus={initialFocus}
      returnFocusRef={buttonRef} menuId={menuId} ariaLabel={`Меню задачи: ${task.title}`} />
  </>;
}
