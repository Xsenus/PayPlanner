import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { Acts } from './Acts';
import type { Act, ActInput } from '../../types';
const state = vi.hoisted(() => ({ create: vi.fn(), success: vi.fn(), canCreate: true }));
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { id: 7, role: { id: 1 } } }) }));
vi.mock('../../hooks/useRolePermissions', () => ({ useRolePermissions: () => ({ acts: { canView: true, canCreate: state.canCreate, canEdit: false, canDelete: false }, accounts: { canView: true }, clients: { canView: true } }) }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => ({ success: state.success, error: vi.fn() }) }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm: vi.fn() }) }));
vi.mock('../../hooks/useTranslation', () => ({ useTranslation: () => ({ t: () => undefined }) }));
vi.mock('../../hooks/useDocumentStatusSettings', () => ({ useDocumentStatusSettings: () => [] }));
vi.mock('../../hooks/useActs', () => ({ useActs: () => ({ acts: [], loading: false, refreshing: false, error: null,
  summary: null, summaryLoading: false, pagination: { total: 0, page: 1, pageSize: 20 }, refresh: vi.fn(), createAct: state.create }) }));
vi.mock('../../hooks/usePersistedTableSettings', () => ({ usePersistedTableSettings: () => ({ columnOrder: ['number'], orderedVisibleColumns: ['number'], visibleColumns: ['number'],
  pageSize: 20, setPageSize: vi.fn(), toggleColumn: vi.fn(), moveColumn: vi.fn(), handleColumnDragStart: vi.fn(), handleColumnDragOver: vi.fn(), handleColumnDrop: vi.fn(),
  handleColumnDragEnd: vi.fn(), handleResizeStart: vi.fn(), getColumnWidth: () => 160, getTableWidth: () => 160, resetTableSettings: vi.fn() }) }));
vi.mock('../../services/api', () => ({ apiService: { getClients: vi.fn().mockResolvedValue([]), getActResponsibles: vi.fn().mockResolvedValue([]) } }));
vi.mock('../ui/TableColumnSettings', () => ({ TableColumnSettings: () => null }));
vi.mock('../DateRange/DatePeriodPicker', () => ({ DatePeriodPicker: () => null }));
const key = '11111111-1111-4111-8111-111111111111';
const body: ActInput = { number: '', date: '2027-01-01', amount: 100, status: 'Created', clientId: 7 };
vi.mock('./ActModal', () => ({ ActModal: (props: { open: boolean; submitting: boolean; onSubmit: (body: ActInput, options: { attemptKey: string }) => Promise<unknown> }) => props.open
  ? <button disabled={props.submitting} onClick={() => { void props.onSubmit(body, { attemptKey: key }).catch(() => undefined); }}>Принять акт</button> : null }));
const saved = { ...body, id: 91, number: '1' } as Act;
beforeEach(() => {
  state.canCreate = true; state.create.mockReset().mockResolvedValue(saved); state.success.mockReset();
  localStorage.clear(); sessionStorage.clear();
  localStorage.setItem('pp_token', `synthetic.${btoa(JSON.stringify({ sub: '7' }))}.signature`);
  localStorage.setItem('pp.auth-session-epoch.v1', 'acts-session');
});
afterEach(() => { localStorage.clear(); sessionStorage.clear(); });
function clickCreateEntry(index: 0 | 1) {
  const entries = screen.getAllByRole('button', { name: 'Добавить акт' });
  expect(entries).toHaveLength(2);
  fireEvent.click(entries[index]);
}
it.each([0, 1] as const)('act journal entry %s opens the dedicated create page without a manual number', (index) => {
  const open = vi.fn(); render(<Acts onCreateActPage={open} />);
  clickCreateEntry(index);
  expect(open).toHaveBeenCalledWith({ status: 'Created' });
});
it('act journal fallback forwards the UUID, accepts the canonical act and closes its modal', async () => {
  render(<Acts />);
  clickCreateEntry(1);
  fireEvent.click(screen.getByRole('button', { name: 'Принять акт' }));
  await waitFor(() => expect(state.create).toHaveBeenCalledWith(body, expect.any(Function), { attemptKey: key }));
  await waitFor(() => expect(screen.queryByRole('button', { name: 'Принять акт' })).toBeNull());
  expect(state.success).toHaveBeenCalledWith('Акт создан.');
});
it('act journal ignores a late create after its permission is revoked', async () => {
  let resolve!: (act: Act) => void; state.create.mockReturnValue(new Promise<Act>(done => { resolve = done; }));
  const view = render(<Acts />);
  clickCreateEntry(0);
  fireEvent.click(screen.getByRole('button', { name: 'Принять акт' }));
  state.canCreate = false; view.rerender(<Acts />);
  await act(async () => { resolve(saved); });
  expect(state.success).not.toHaveBeenCalled();
});
