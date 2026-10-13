import { createPortal } from 'react-dom';
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type RefObject } from 'react';

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
  keyboardNavigation?: boolean;
  initialFocus?: 'first' | 'last';
  returnFocusRef?: RefObject<HTMLElement>;
  menuId?: string;
  ariaLabel?: string;
}

export function TableContextMenu({
  open,
  position,
  title,
  actions,
  onClose,
  keyboardNavigation = false,
  initialFocus = 'first',
  returnFocusRef,
  menuId,
  ariaLabel,
}: TableContextMenuProps) {
  const menuRef = useRef<HTMLDivElement | null>(null);
  const restoreFocusOnClose = useRef(true);
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

  useLayoutEffect(() => {
    if (!open || !keyboardNavigation || !resolvedPosition || !menuRef.current
      || menuRef.current.contains(document.activeElement)) return;
    const buttons = menuRef.current.querySelectorAll<HTMLButtonElement>('button[role="menuitem"]:not(:disabled)');
    buttons[initialFocus === 'last' ? buttons.length - 1 : 0]?.focus({ preventScroll: true });
  }, [open, keyboardNavigation, initialFocus, resolvedPosition]);

  useEffect(() => {
    if (!open || !keyboardNavigation) return;
    restoreFocusOnClose.current = true;
    const trigger = returnFocusRef?.current;
    return () => { if (restoreFocusOnClose.current && trigger?.isConnected) trigger.focus({ preventScroll: true }); };
  }, [open, keyboardNavigation, returnFocusRef]);

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
        id={menuId}
        role="menu"
        aria-label={ariaLabel}
        className="absolute min-w-[220px] rounded-xl border border-slate-200 bg-white py-2 shadow-2xl"
        style={{ top: resolvedPosition.y, left: resolvedPosition.x }}
        onClick={(event) => event.stopPropagation()}
        onKeyDown={(event) => {
          if (!keyboardNavigation) return;
          if (event.key === 'Escape') {
            event.preventDefault();
            event.stopPropagation();
            onClose();
          } else if (event.key === 'Tab') {
            event.preventDefault();
            const trigger = returnFocusRef?.current;
            const styleCache = new Map<HTMLElement, CSSStyleDeclaration>();
            const controls = Array.from(document.querySelectorAll<HTMLElement>('button, a[href], input, select, textarea, [tabindex]'))
              .filter((element) => {
                if (element.tabIndex < 0 || element.matches(':disabled') || menuRef.current?.contains(element)
                  || element.closest('[hidden], [inert], [aria-hidden="true"]')) return false;
                for (let parent: HTMLElement | null = element; parent; parent = parent.parentElement) {
                  let style = styleCache.get(parent);
                  if (!style) { style = window.getComputedStyle(parent); styleCache.set(parent, style); }
                  if (style.display === 'none' || style.visibility === 'hidden') return false;
                }
                return true;
              });
            const index = trigger ? controls.indexOf(trigger) : -1;
            const target = index >= 0 ? controls[index + (event.shiftKey ? -1 : 1)] ?? trigger : trigger;
            restoreFocusOnClose.current = false;
            target?.focus({ preventScroll: true });
            onClose();
          } else if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
            event.preventDefault();
            const buttons = Array.from(menuRef.current?.querySelectorAll<HTMLButtonElement>('button[role="menuitem"]:not(:disabled)') ?? []);
            if (buttons.length === 0) return;
            const active = buttons.indexOf(document.activeElement as HTMLButtonElement);
            const index = event.key === 'Home' ? 0 : event.key === 'End' ? buttons.length - 1
              : (active + (event.key === 'ArrowDown' ? 1 : -1) + buttons.length) % buttons.length;
            buttons[index]?.focus({ preventScroll: true });
          }
        }}
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
