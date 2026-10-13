import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { downloadCaseTaskCsv } from '../../utils/caseTaskCsv';
import type { TaskJournalItem } from '../../utils/taskJournalStore';
import { CaseTaskRowMenu } from './CaseTaskRowMenu';

vi.mock('../../utils/caseTaskCsv', async (original) => ({ ...await original<typeof import('../../utils/caseTaskCsv')>(), downloadCaseTaskCsv: vi.fn() }));
const task = { id: 19, title: 'Синтетическая задача', status: 'new' } as TaskJournalItem;
const toggle = vi.fn(); const openTask = vi.fn();
function View({ currentTask = task, disabled = false, canSelect = true, selected = false, canExport = true, canOpen = true, handler = openTask }) {
  return <CaseTaskRowMenu task={currentTask} caseId={3} disabled={disabled} canSelect={canSelect} selected={selected}
    onToggle={toggle} canExport={canExport} statusLabels={{ new: 'Новая' }} onOpenTask={canOpen ? handler : undefined} />;
}
const openMenu = () => fireEvent.click(screen.getByRole('button', { name: 'Действия задачи: Синтетическая задача' }));
beforeEach(() => vi.clearAllMocks());

describe('case task row menu', () => {
  it('opens the common card with exactly the original task ID', () => {
    render(<View />); openMenu(); fireEvent.click(screen.getByRole('menuitem', { name: 'Открыть карточку' }));
    expect(openTask).toHaveBeenCalledExactlyOnceWith(19); expect(screen.queryByRole('menu')).toBeNull();
  });
  it('only exposes the common read action for a role without edit/export', () => {
    render(<View canSelect={false} canExport={false} />); openMenu();
    expect(screen.getAllByRole('menuitem')).toHaveLength(1);
    expect(screen.getByRole('menuitem', { name: 'Открыть карточку' })).toBeTruthy();
  });
  it('has no decorative button when no action is available', () => {
    render(<View canSelect={false} canExport={false} canOpen={false} />);
    expect(screen.queryByRole('button')).toBeNull();
  });
  it.each([false, true])('toggles the existing selection (selected %s)', (selected) => {
    render(<View selected={selected} />); openMenu();
    fireEvent.click(screen.getByRole('menuitem', { name: selected ? 'Снять выбор задачи' : 'Выбрать задачу' }));
    expect(toggle).toHaveBeenCalledOnce(); expect(openTask).not.toHaveBeenCalled();
  });
  it('exports only this visible row', () => {
    render(<View />); openMenu(); fireEvent.click(screen.getByRole('menuitem', { name: 'Экспорт задачи CSV' }));
    expect(downloadCaseTaskCsv).toHaveBeenCalledWith('case-3-task-19.csv', expect.stringContaining('"19";"Синтетическая задача"'));
    expect(toggle).not.toHaveBeenCalled();
  });
  it('closes when data or privileges change', () => {
    const { rerender } = render(<View />); openMenu(); rerender(<View canExport={false} />);
    expect(screen.queryByRole('menu')).toBeNull(); openMenu();
    rerender(<View currentTask={{ ...task, id: 88 }} />); expect(screen.queryByRole('menu')).toBeNull();
  });
  it('closes and disables actions during loading/saving', () => {
    const { rerender } = render(<View />); openMenu(); rerender(<View disabled />);
    expect(screen.queryByRole('menu')).toBeNull();
    expect((screen.getByRole('button') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('button')); expect(screen.queryByRole('menu')).toBeNull();
  });
  it('closes on viewport resize', () => {
    render(<View />); openMenu(); fireEvent(window, new Event('resize')); expect(screen.queryByRole('menu')).toBeNull();
  });
  it('follows scrolling while its row stays visible, then closes when the row leaves the viewport', () => {
    let top = 120;
    const bounds = vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
      const y = this.getAttribute('aria-haspopup') === 'menu' ? top : 0;
      return { x: 20, y, top: y, bottom: y + 32, left: 20, right: 52, width: 32, height: 32, toJSON: () => ({}) };
    });
    try {
      render(<View />); openMenu(); top = 80; fireEvent(window, new Event('scroll'));
      expect(screen.getByRole('menu').style.top).toBe('116px');
      top = -40; fireEvent(window, new Event('scroll')); expect(screen.queryByRole('menu')).toBeNull();
    } finally { bounds.mockRestore(); }
  });
  it('ArrowUp opens the last action and Escape returns to the row', async () => {
    const user = userEvent.setup(); render(<View />); const trigger = screen.getByRole('button'); trigger.focus();
    await user.keyboard('{ArrowUp}');
    expect(document.activeElement).toBe(screen.getByRole('menuitem', { name: 'Экспорт задачи CSV' }));
    await user.keyboard('{Escape}'); expect(document.activeElement).toBe(trigger);
    expect(trigger.getAttribute('aria-expanded')).toBe('false');
  });
  it('keeps the menu open when a callback identity changes and uses the latest callback', async () => {
    const next = vi.fn(); const { rerender } = render(<View />); openMenu(); rerender(<View handler={next} />);
    await waitFor(() => expect(screen.getByRole('menu')).toBeTruthy());
    fireEvent.click(screen.getByRole('menuitem', { name: 'Открыть карточку' })); expect(next).toHaveBeenCalledWith(19); expect(openTask).not.toHaveBeenCalled();
  });
});
