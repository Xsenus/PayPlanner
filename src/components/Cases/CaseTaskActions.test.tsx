import { useState } from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from '../../services/api';
import { downloadCaseTaskCsv } from '../../utils/caseTaskCsv';
import type { TaskJournalItem } from '../../utils/taskJournalStore';
import { CaseTaskActions } from './CaseTaskActions';

const confirm = vi.hoisted(() => vi.fn());
const actor = vi.hoisted(() => ({ user: { id: 7 } }));
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => actor }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm }) }));
vi.mock('../../services/api', () => ({ apiService: { bulkUpdateTaskJournalItems: vi.fn(), getTaskJournalAssignees: vi.fn(), getTaskJournalItem: vi.fn() } }));
vi.mock('../../utils/caseTaskCsv', async (original) => ({ ...await original<typeof import('../../utils/caseTaskCsv')>(), downloadCaseTaskCsv: vi.fn() }));

const rows = [{ id: 10, title: 'Первая', status: 'new' }, { id: 11, title: 'Вторая', status: 'new' }] as TaskJournalItem[];
const changed = vi.fn();
function View({ tasks = rows, canEdit = true, canEditBilling = canEdit, canExport = true, loading = false, scope = '3:all:1', remount = true }: {
  tasks?: TaskJournalItem[]; canEdit?: boolean; canEditBilling?: boolean; canExport?: boolean; loading?: boolean; scope?: string; remount?: boolean;
}) {
  const [message, setMessage] = useState('');
  return <><p role="status">{message}</p><CaseTaskActions key={remount ? scope : 'stable'} scopeKey={scope} tasks={tasks} caseId={3} loading={loading} canEdit={canEdit} canEditBilling={canEditBilling} canExport={canExport} statusLabels={{ new: 'Новая' }} onChanged={(value) => { changed(value); setMessage(value); }}>
    {(selection) => <div>
      {selection.enabled ? <>
        <button disabled={selection.busy} onClick={selection.toggleAll}>Выбрать страницу</button>
        {tasks.map((task) => <button key={task.id} disabled={selection.busy} aria-pressed={selection.selected.has(task.id)} onClick={() => selection.toggle(task.id)}>{task.title}</button>)}
      </> : null}
      <span data-testid="selection">{[...selection.selected].join(',')}</span>
    </div>}
  </CaseTaskActions></>;
}
const selectPage = () => fireEvent.click(screen.getByRole('button', { name: 'Выбрать страницу' }));
const apply = () => fireEvent.click(screen.getByRole('button', { name: 'Применить к выбранным' }));
const changeSelect = (label: string, value: string) => fireEvent.change(screen.getByLabelText(label, { selector: 'select' }), { target: { value } });

beforeEach(() => { vi.clearAllMocks(); confirm.mockResolvedValue(true); vi.mocked(apiService.bulkUpdateTaskJournalItems).mockResolvedValue({ requested: 2, updated: 2, skipped: 0 }); });

describe('case task page actions', () => {
  it('supports a separate billing-only permission without granting old status/assignee writes', async () => {
    vi.mocked(apiService.bulkUpdateTaskJournalItems).mockResolvedValueOnce({ requested: 2, updated: 2, unchanged: 0, skipped: 0, conflicted: 0,
      items: [10, 11].map((id) => ({ id, outcome: 'updated', reason: null, updatedAt: '2026-10-24T02:03:04.7654321Z' })) });
    render(<View canEdit={false} canEditBilling canExport={false} />); selectPage();
    expect(screen.queryByRole('button', { name: 'Применить к выбранным' })).toBeNull();
    expect(screen.queryByLabelText('Массовое действие')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Изменить учёт выбранных' }));
    fireEvent.click(screen.getByRole('button', { name: 'Изменить учёт' }));
    await waitFor(() => expect(changed).toHaveBeenCalledOnce());
    expect(apiService.bulkUpdateTaskJournalItems).toHaveBeenCalledWith(expect.objectContaining({ billingClassification: 'internal' }));
    expect(apiService.getTaskJournalAssignees).not.toHaveBeenCalled();
  });
  it('uses the shared billing dialog and keeps failed selected IDs instead of clearing all rows', async () => {
    vi.mocked(apiService.bulkUpdateTaskJournalItems).mockResolvedValue({ requested: 2, updated: 1, unchanged: 0, skipped: 1, conflicted: 0,
      items: [{ id: 10, outcome: 'updated', reason: null, updatedAt: '2026-10-24T01:02:03.1234567Z' },
        { id: 11, outcome: 'skipped', reason: 'contractual', updatedAt: null }] });
    render(<View remount={false} />); selectPage();
    fireEvent.click(screen.getByRole('button', { name: 'Изменить учёт выбранных' }));
    fireEvent.click(screen.getByRole('button', { name: 'Изменить учёт' }));
    await waitFor(() => expect(changed).toHaveBeenCalledOnce());
    expect(apiService.bulkUpdateTaskJournalItems).toHaveBeenCalledExactlyOnceWith({ ids: [10, 11], billingClassification: 'internal', includeCompleted: false, expectedUpdatedAtById: { '10': null, '11': null } });
    expect(screen.getByTestId('selection').textContent).toBe('11');
    expect(changed).toHaveBeenCalledWith(expect.stringContaining('Изменено: 1'));
  });
  it('preserves a pending billing result across page changes without refreshing the new scope', async () => {
    let resolve!: (value: Awaited<ReturnType<typeof apiService.bulkUpdateTaskJournalItems>>) => void;
    vi.mocked(apiService.bulkUpdateTaskJournalItems).mockImplementation(() => new Promise((done) => { resolve = done; }));
    const { rerender } = render(<View remount={false} />); selectPage();
    fireEvent.click(screen.getByRole('button', { name: 'Изменить учёт выбранных' }));
    fireEvent.click(screen.getByRole('button', { name: 'Изменить учёт' }));
    await waitFor(() => expect(apiService.bulkUpdateTaskJournalItems).toHaveBeenCalledOnce());
    rerender(<View remount={false} scope="3:all:2" tasks={[{ ...rows[0], id: 99 }]} />);
    expect(screen.getByRole('button', { name: 'Ожидание операции…' })).toBeTruthy();
    await act(async () => resolve({ requested: 2, updated: 2, unchanged: 0, skipped: 0, conflicted: 0,
      items: [10, 11].map((id) => ({ id, outcome: 'updated', reason: null, updatedAt: '2026-10-24T02:03:04.7654321Z' })) }));
    expect(changed).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Построчный итог смены учёта').textContent).toContain('#11');
    expect(screen.getByLabelText('Построчный итог смены учёта').textContent).not.toContain('#99');
  });
  it('offers no selection or actions to a role without edit/export', () => {
    render(<View canEdit={false} canExport={false} />);
    expect(screen.queryByRole('button')).toBeNull();
    expect(apiService.getTaskJournalAssignees).not.toHaveBeenCalled();
  });
  it('supports export-only roles without exposing writes or loading the directory', () => {
    render(<View canEdit={false} />); fireEvent.click(screen.getByRole('button', { name: 'Вторая' }));
    fireEvent.click(screen.getByRole('button', { name: 'Экспорт выбранных CSV' }));
    expect(vi.mocked(downloadCaseTaskCsv).mock.calls[0][1]).toContain('Вторая');
    expect(vi.mocked(downloadCaseTaskCsv).mock.calls[0][1]).not.toContain('Первая');
    expect(screen.queryByRole('button', { name: 'Применить к выбранным' })).toBeNull();
    expect(apiService.getTaskJournalAssignees).not.toHaveBeenCalled();
  });
  it('posts only selected visible IDs, reports partial results honestly and clears the selection', async () => {
    vi.mocked(apiService.bulkUpdateTaskJournalItems).mockResolvedValue({ requested: 2, updated: 1, skipped: 1 });
    render(<View />); selectPage(); apply();
    await screen.findByText(/Изменено: 1. Пропущено: 1/);
    expect(apiService.bulkUpdateTaskJournalItems).toHaveBeenCalledExactlyOnceWith({ ids: [10, 11], status: 'inProgress' });
    expect(changed).toHaveBeenCalledOnce();
    expect(screen.getByTestId('selection').textContent).toBe('');
    expect(confirm).toHaveBeenCalledWith(expect.objectContaining({ title: 'Изменить выбранные задачи (2)?' }));
  });
  it('respects cancelled confirmation', async () => {
    confirm.mockResolvedValue(false); render(<View />); selectPage(); apply();
    await waitFor(() => expect((screen.getByRole('button', { name: 'Применить к выбранным' }) as HTMLButtonElement).disabled).toBe(false));
    expect(apiService.bulkUpdateTaskJournalItems).not.toHaveBeenCalled();
    expect(screen.getByTestId('selection').textContent).toBe('10,11');
  });
  it('keeps selection on request failure and permits retry', async () => {
    vi.mocked(apiService.bulkUpdateTaskJournalItems).mockRejectedValueOnce(new Error('Временная ошибка'));
    render(<View />); selectPage(); apply(); await screen.findByRole('alert');
    expect(screen.getByTestId('selection').textContent).toBe('10,11');
    expect(changed).not.toHaveBeenCalled(); apply(); await screen.findByText(/Изменено: 2/);
  });
  it('drops selection across filter/page/case changes and new responses', async () => {
    const { rerender } = render(<View />); selectPage(); rerender(<View scope="3:active:2" />);
    expect(screen.getByTestId('selection').textContent).toBe(''); selectPage();
    rerender(<View scope="3:active:2" tasks={[{ id: 77, title: 'Другая' } as TaskJournalItem]} />);
    await waitFor(() => expect(screen.getByTestId('selection').textContent).toBe(''));
  });
  it('loads assignees on demand and sends an isolated assignee change', async () => {
    vi.mocked(apiService.getTaskJournalAssignees).mockResolvedValue([{ id: 4, name: 'Исполнитель', email: 'fixture.local' }]);
    render(<View canExport={false} />); selectPage();
    expect(screen.queryByRole('button', { name: 'Экспорт выбранных CSV' })).toBeNull();
    changeSelect('Массовое действие', 'assignee');
    await waitFor(() => expect((screen.getByLabelText('Новый исполнитель выбранных задач', { selector: 'select' }) as HTMLSelectElement).disabled).toBe(false));
    changeSelect('Новый исполнитель выбранных задач', '4'); apply();
    await screen.findByText(/Изменено: 2/);
    expect(apiService.bulkUpdateTaskJournalItems).toHaveBeenCalledExactlyOnceWith({ ids: [10, 11], assigneeUserId: 4 });
    expect(apiService.getTaskJournalAssignees).toHaveBeenCalledOnce();
  });
  it('does not send a mutation if navigation occurs while confirmation is pending', async () => {
    let resolve!: (value: boolean) => void; confirm.mockImplementation(() => new Promise<boolean>((done) => { resolve = done; }));
    const { rerender } = render(<View />); selectPage(); apply(); rerender(<View scope="4:all:1" />);
    await act(async () => resolve(true));
    expect(apiService.bulkUpdateTaskJournalItems).not.toHaveBeenCalled();
    expect(changed).not.toHaveBeenCalled();
  });
  it('does not refresh a new case from an old mutation response', async () => {
    let resolve!: (value: { requested: number; updated: number; skipped: number }) => void;
    vi.mocked(apiService.bulkUpdateTaskJournalItems).mockImplementation(() => new Promise((done) => { resolve = done; }));
    const { rerender } = render(<View />); selectPage(); apply();
    await waitFor(() => expect(apiService.bulkUpdateTaskJournalItems).toHaveBeenCalledOnce());
    expect((screen.getByRole('button', { name: 'Сохранение…' }) as HTMLButtonElement).disabled).toBe(true);
    rerender(<View scope="4:all:1" />); await act(async () => resolve({ requested: 2, updated: 2, skipped: 0 }));
    expect(changed).not.toHaveBeenCalled(); expect(screen.getByRole('status').textContent).toBe('');
  });
  it('does not apply an outdated selection after refreshing the same page during confirmation', async () => {
    let resolve!: (value: boolean) => void;
    confirm.mockImplementation(() => new Promise<boolean>((done) => { resolve = done; }));
    const { rerender } = render(<View />); selectPage(); apply();
    rerender(<View tasks={[{ id: 77, title: 'Новая строка' } as TaskJournalItem]} />);
    await act(async () => resolve(true));
    expect(apiService.bulkUpdateTaskJournalItems).not.toHaveBeenCalled();
  });
  it('does not offer mass approval decisions', () => {
    render(<View />); selectPage();
    expect(screen.getByLabelText('Новый статус выбранных задач', { selector: 'select' }).innerHTML).not.toContain('value="approved"');
  });
  it('retries a failed assignee directory without losing selected rows', async () => {
    vi.mocked(apiService.getTaskJournalAssignees).mockRejectedValueOnce(new Error('Нет связи'))
      .mockResolvedValueOnce([{ id: 4, name: 'Исполнитель', email: 'fixture.local' }]);
    render(<View />); selectPage(); changeSelect('Массовое действие', 'assignee');
    await screen.findByRole('alert');
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку исполнителей' }));
    await waitFor(() => expect((screen.getByLabelText('Новый исполнитель выбранных задач', { selector: 'select' }) as HTMLSelectElement).disabled).toBe(false));
    expect(screen.getByTestId('selection').textContent).toBe('10,11');
    expect(apiService.getTaskJournalAssignees).toHaveBeenCalledTimes(2);
  });
  it('ignores duplicate apply clicks during pending confirmation', async () => {
    let resolve!: (value: boolean) => void;
    confirm.mockImplementation(() => new Promise<boolean>((done) => { resolve = done; }));
    render(<View />); selectPage(); apply();
    fireEvent.click(screen.getByRole('button', { name: 'Сохранение…' }));
    expect(confirm).toHaveBeenCalledOnce();
    await act(async () => resolve(true));
    await screen.findByText(/Изменено: 2/);
    expect(apiService.bulkUpdateTaskJournalItems).toHaveBeenCalledOnce();
  });
  it('revokes selection when permissions disappear or a response begins loading', () => {
    const { rerender } = render(<View />); selectPage(); rerender(<View loading />);
    expect(screen.getByTestId('selection').textContent).toBe('');
    rerender(<View />); selectPage(); rerender(<View canEdit={false} canExport={false} />);
    expect(screen.getByTestId('selection').textContent).toBe('');
    expect(screen.queryByRole('button')).toBeNull();
  });
});
