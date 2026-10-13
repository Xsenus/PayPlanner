import { useRef, useState } from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { TableContextMenu, type TableContextMenuAction } from './TableContextMenu';

const close = vi.fn();
const select = vi.fn();
const actions: TableContextMenuAction[] = [
  { key: 'disabled', label: 'Недоступно', disabled: true, onSelect: select },
  { key: 'first', label: 'Первое', onSelect: select },
  { key: 'second', label: 'Второе', onSelect: select },
  { key: 'third', label: 'Третье', onSelect: select },
];
function Harness({ keyboard = true, initialFocus = 'first' as 'first' | 'last', x = 40, y = 40 }) {
  const ref = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useState(false);
  return <>
    <button ref={ref} onClick={() => setOpen(true)}>Открыть</button>
    <button>Следующее поле</button>
    <TableContextMenu open={open} position={open ? { x, y } : null} actions={actions}
      keyboardNavigation={keyboard} initialFocus={initialFocus} returnFocusRef={ref}
      menuId="owned-menu" ariaLabel="Действия строки" onClose={() => { close(); setOpen(false); }} />
  </>;
}
beforeEach(() => vi.clearAllMocks());

describe('opt-in table menu keyboard navigation', () => {
  it('focuses enabled items, wraps arrows and supports Home/End', async () => {
    const user = userEvent.setup(); render(<Harness />);
    await user.click(screen.getByRole('button', { name: 'Открыть' }));
    const first = screen.getByRole('menuitem', { name: 'Первое' });
    expect(document.activeElement).toBe(first);
    await user.keyboard('{ArrowUp}'); expect(document.activeElement).toBe(screen.getByRole('menuitem', { name: 'Третье' }));
    await user.keyboard('{ArrowDown}'); expect(document.activeElement).toBe(first);
    await user.keyboard('{End}'); expect(document.activeElement).toBe(screen.getByRole('menuitem', { name: 'Третье' }));
    await user.keyboard('{Home}'); expect(document.activeElement).toBe(first);
    await user.keyboard('{ArrowDown}'); expect(document.activeElement).toBe(screen.getByRole('menuitem', { name: 'Второе' }));
    expect(screen.getByRole('menu', { name: 'Действия строки' }).id).toBe('owned-menu');
  });
  it('can open on the last enabled item', async () => {
    const user = userEvent.setup(); render(<Harness initialFocus="last" />);
    await user.click(screen.getByRole('button', { name: 'Открыть' }));
    expect(document.activeElement).toBe(screen.getByRole('menuitem', { name: 'Третье' }));
  });
  it('Escape closes once and restores the trigger without scrolling', async () => {
    const user = userEvent.setup(); render(<Harness />);
    const trigger = screen.getByRole('button', { name: 'Открыть' });
    await user.click(trigger); await user.keyboard('{Escape}');
    expect(screen.queryByRole('menu')).toBeNull(); expect(close).toHaveBeenCalledOnce();
    expect(document.activeElement).toBe(trigger);
  });
  it('Tab closes and continues from the trigger', async () => {
    const user = userEvent.setup(); render(<Harness />);
    await user.click(screen.getByRole('button', { name: 'Открыть' })); await user.tab();
    expect(screen.queryByRole('menu')).toBeNull();
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Следующее поле' }));
  });
  it('Shift+Tab closes and goes to the preceding visible field', async () => {
    const user = userEvent.setup(); render(<><button>Предыдущее поле</button><Harness /></>);
    await user.click(screen.getByRole('button', { name: 'Открыть' })); await user.tab({ shift: true });
    expect(screen.queryByRole('menu')).toBeNull();
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Предыдущее поле' }));
  });
  it('Enter invokes the focused action once, closes and restores focus', async () => {
    const user = userEvent.setup(); render(<Harness />);
    const trigger = screen.getByRole('button', { name: 'Открыть' });
    await user.click(trigger); await user.keyboard('{Enter}');
    expect(select).toHaveBeenCalledOnce(); expect(screen.queryByRole('menu')).toBeNull();
    expect(document.activeElement).toBe(trigger);
  });
  it('leaves legacy callers without automatic focus or arrow handling', async () => {
    const user = userEvent.setup(); render(<Harness keyboard={false} />);
    const trigger = screen.getByRole('button', { name: 'Открыть' });
    await user.click(trigger); expect(document.activeElement).toBe(trigger);
    const first = screen.getByRole('menuitem', { name: 'Первое' }); first.focus();
    fireEvent.keyDown(first, { key: 'ArrowDown' }); expect(document.activeElement).toBe(first);
    fireEvent.keyDown(window, { key: 'Escape' }); expect(close).toHaveBeenCalledOnce();
  });
  it('clamps the portal to the viewport', async () => {
    const originalWidth = window.innerWidth; const originalHeight = window.innerHeight;
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 390 });
    Object.defineProperty(window, 'innerHeight', { configurable: true, value: 300 });
    const bounds = vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue({
      width: 240, height: 180, x: 0, y: 0, top: 0, left: 0, right: 240, bottom: 180, toJSON: () => ({}),
    });
    try {
      render(<Harness x={380} y={280} />); fireEvent.click(screen.getByRole('button', { name: 'Открыть' }));
      await waitFor(() => { const menu = screen.getByRole('menu'); expect(menu.style.left).toBe('138px'); expect(menu.style.top).toBe('108px'); });
    } finally { bounds.mockRestore(); Object.defineProperty(window, 'innerWidth', { value: originalWidth }); Object.defineProperty(window, 'innerHeight', { value: originalHeight }); }
  });
});
