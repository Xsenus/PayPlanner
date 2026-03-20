import { createPortal } from 'react-dom';
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';

export type TableContextMenuAction = {
  key: string;
  label: string;
  onSelect: () => void | Promise<void>;
  disabled?: boolean;
  variant?: 'default' | 'danger';
  separatorBefore?: boolean;
};

export type TableContextMenuPosition = {
  x: number;
  y: number;
};

interface TableContextMenuProps {
  open: boolean;
  position: TableContextMenuPosition | null;
  title?: string;
  actions: TableContextMenuAction[];
  onClose: () => void;
}

export function TableContextMenu({
  open,
  position,
  title,
  actions,
  onClose,
}: TableContextMenuProps) {
  const menuRef = useRef<HTMLDivElement | null>(null);
  const [resolvedPosition, setResolvedPosition] = useState<TableContextMenuPosition | null>(position);

  const visibleActions = useMemo(() => actions.filter((action) => action.label.trim().length > 0), [actions]);

  useEffect(() => {
    setResolvedPosition(position);
  }, [position]);

  useLayoutEffect(() => {
    if (!open || !position || !menuRef.current) return;

    const next = { ...position };
    const bounds = menuRef.current.getBoundingClientRect();
    const viewportPadding = 12;

    if (next.x + bounds.width > window.innerWidth - viewportPadding) {
      next.x = Math.max(window.innerWidth - bounds.width - viewportPadding, viewportPadding);
    }

    if (next.y + bounds.height > window.innerHeight - viewportPadding) {
      next.y = Math.max(window.innerHeight - bounds.height - viewportPadding, viewportPadding);
    }

    if (next.x !== resolvedPosition?.x || next.y !== resolvedPosition?.y) {
      setResolvedPosition(next);
    }
  }, [open, position, resolvedPosition?.x, resolvedPosition?.y, title, visibleActions]);

  useEffect(() => {
    if (!open) return;

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        onClose();
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => {
      window.removeEventListener('keydown', handleKeyDown);
    };
  }, [onClose, open]);

  if (!open || !resolvedPosition || typeof document === 'undefined') return null;

  return createPortal(
    <div
      className="fixed inset-0 z-[2200]"
      role="presentation"
      onClick={onClose}
      onContextMenu={(event) => {
        event.preventDefault();
        onClose();
      }}
    >
      <div
        ref={menuRef}
        role="menu"
        className="absolute min-w-[220px] rounded-xl border border-slate-200 bg-white py-2 shadow-2xl"
        style={{ top: resolvedPosition.y, left: resolvedPosition.x }}
        onClick={(event) => event.stopPropagation()}
      >
        {title ? (
          <>
            <div className="px-4 py-2 text-xs font-semibold uppercase tracking-wide text-slate-400">
              {title}
            </div>
            <div className="my-1 border-t border-slate-100" />
          </>
        ) : null}

        {visibleActions.map((action) => {
          const textClass =
            action.variant === 'danger'
              ? action.disabled
                ? 'text-rose-300'
                : 'text-rose-600 hover:bg-rose-50'
              : action.disabled
                ? 'text-slate-300'
                : 'text-slate-700 hover:bg-slate-100';

          return (
            <div key={action.key}>
              {action.separatorBefore ? <div className="my-1 border-t border-slate-100" /> : null}
              <button
                type="button"
                role="menuitem"
                disabled={action.disabled}
                onClick={() => {
                  onClose();
                  void action.onSelect();
                }}
                className={`flex w-full items-center px-4 py-2 text-left text-sm transition-colors ${textClass}`}
              >
                {action.label}
              </button>
            </div>
          );
        })}
      </div>
    </div>,
    document.body,
  );
}
