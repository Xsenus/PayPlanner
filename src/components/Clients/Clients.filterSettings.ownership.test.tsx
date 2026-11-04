import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Clients } from './Clients';
import { apiService } from '../../services/api';
import { documentCopyTestSession } from '../../test/documentCopySession';

const ports = vi.hoisted(() => ({ actor: 7, role: 4, permissions: Object.fromEntries(['clients', 'cases', 'contracts', 'accounts', 'acts', 'payments', 'tasks', 'audit', 'dictionaries', 'legalEntities', 'companyProfile'].map(key => [key, { canView: false, canCreate: false, canEdit: false, canDelete: false, viewScope: 'all' }])) }));
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { id: ports.actor, role: { id: ports.role } } }) }));
vi.mock('../../hooks/useRolePermissions', () => ({ useRolePermissions: () => ports.permissions }));
vi.mock('../../hooks/useTranslation', () => ({ useTranslation: () => ({ t: (key: string) => key }) }));
vi.mock('../../hooks/useUserTimeZone', () => ({ useUserTimeZone: () => 'UTC' }));
vi.mock('../../hooks/useClientStatuses', () => ({ useClientStatuses: () => ({ statuses: [] }) }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm: vi.fn() }) }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => ({ error: vi.fn(), success: vi.fn(), info: vi.fn() }) }));
vi.mock('./ClientModal', () => ({ ClientModal: () => null }));
vi.mock('./ClientDetail', () => ({ ClientDetail: () => null }));
vi.mock('./CaseModal', () => ({ CaseModal: () => null }));
vi.mock('../../hooks/usePersistedTableSettings', async () => {
  const actual = await vi.importActual<typeof import('../../hooks/usePersistedTableSettings')>('../../hooks/usePersistedTableSettings');
  return { ...actual, usePersistedTableSettings: (options: Parameters<typeof actual.usePersistedTableSettings>[0]) => actual.usePersistedTableSettings({ ...options, enabled: false, persistLocally: false }) };
});
vi.mock('../../services/api', () => ({ apiService: { getClientsV2: vi.fn(), getUserSetting: vi.fn(), upsertUserSetting: vi.fn() } }));
const key = 'clients_table_filters';
const setting = (value = 'Первичный') => ({ key, value: JSON.stringify({ filters: { name: value }, filterColumns: ['name'] }) });
type Setting = Awaited<ReturnType<typeof apiService.getUserSetting>>;
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(next => { resolve = next; }); return { promise, resolve }; }
async function settle() { await act(async () => { for (let i = 0; i < 12; i++) await Promise.resolve(); }); }
async function clock(ms: number) { await act(async () => { await vi.advanceTimersByTimeAsync(ms); }); }
const writes = () => vi.mocked(apiService.upsertUserSetting).mock.calls.filter(([name]) => name === key);
const reads = () => vi.mocked(apiService.getUserSetting).mock.calls.filter(([name]) => name === key);
async function mount() { const view = render(<Clients />); await settle(); return view; }
function filterInput(value = 'Первичный') {
  if (!screen.queryByRole('heading', { name: 'Фильтры по колонкам' })) {
    fireEvent.click(screen.getByTitle('Таблица')); fireEvent.click(screen.getByRole('button', { name: 'Фильтры' }));
  }
  return screen.getByDisplayValue(value) as HTMLInputElement;
}
function edit(value: string, previous = 'Первичный') { const input = filterInput(previous); fireEvent.change(input, { target: { value } }); return input; }

describe('client filter persistence ownership', () => {
  let session: ReturnType<typeof documentCopyTestSession>;
  beforeEach(() => {
    vi.useFakeTimers(); vi.resetAllMocks(); localStorage.clear(); sessionStorage.clear(); session = documentCopyTestSession(); ports.actor = 7; ports.role = 4;
    Object.values(ports.permissions).forEach(value => Object.assign(value, { canView: false, canCreate: false, canEdit: false, canDelete: false, viewScope: 'all' })); ports.permissions.clients.canView = true;
    vi.mocked(apiService.getClientsV2).mockResolvedValue({ items: [{ id: 1, name: 'Синтетический клиент', phone: '', email: '', company: '', address: '', notes: '', isActive: true, createdAt: '2026-11-04T00:00:00Z' }], total: 1, page: 1, pageSize: 25 });
    vi.mocked(apiService.getUserSetting).mockImplementation(async name => name === key ? setting() : { key: name, value: '' });
    vi.mocked(apiService.upsertUserSetting).mockResolvedValue(setting());
  });
  afterEach(() => { cleanup(); session.restore(); vi.clearAllTimers(); vi.useRealTimers(); });

  it('filter persistence: denied viewer performs no preference reads or writes', async () => {
    ports.permissions.clients.canView = false; await mount(); await clock(1000);
    expect(apiService.getUserSetting).not.toHaveBeenCalled(); expect(writes()).toHaveLength(0);
  });
  it.each(['actor', 'login', 'logout'] as const)('filter persistence: missing response after %s cannot start legacy fallback without a render', async mode => {
    const pending = deferred<Setting>(); vi.mocked(apiService.getUserSetting).mockReturnValueOnce(pending.promise); await mount(); session.replace(mode);
    await act(async () => pending.resolve(null)); await settle(); await clock(1000);
    expect(apiService.getUserSetting).toHaveBeenCalledTimes(1); expect(writes()).toHaveLength(0);
  });
  it.each(['actor', 'login'] as const)('filter persistence: late profile after %s cannot overwrite local cache without a render', async mode => {
    const pending = deferred<Setting>(); vi.mocked(apiService.getUserSetting).mockReturnValueOnce(pending.promise); await mount(); session.replace(mode);
    await act(async () => pending.resolve(setting('Чужой поздний фильтр'))); await settle(); await clock(1000);
    expect(localStorage.getItem('pp.clients_table_filters.7.v1') ?? '').not.toContain('Чужой поздний фильтр'); expect(writes()).toHaveLength(0);
  });
  it('filter persistence: ordinary opening does not rewrite unchanged server preferences', async () => {
    await mount(); expect(filterInput().value).toBe('Первичный'); await clock(2000); expect(reads()).toHaveLength(1); expect(writes()).toHaveLength(0);
  });
  it.each(['actor', 'login', 'logout'] as const)('filter persistence: %s before debounce dispatch blocks the old draft without a render', async mode => {
    await mount(); edit('Мой черновик'); await settle(); await clock(599); expect(writes()).toHaveLength(0); session.replace(mode); await clock(1);
    expect(writes()).toHaveLength(0);
  });
  it('filter persistence: current edits coalesce into one save and token refresh preserves it', async () => {
    await mount(); edit('Первый ввод'); edit('Итоговый ввод', 'Первый ввод'); await settle(); await clock(599); expect(writes()).toHaveLength(0); session.refresh(); await clock(1);
    expect(writes()).toHaveLength(1); expect(JSON.parse(writes()[0][1]).filters.name).toBe('Итоговый ввод'); expect(writes()[0][2]?.signal).toBeInstanceOf(AbortSignal);
    await clock(1200); expect(writes()).toHaveLength(1); expect(reads()).toHaveLength(1);
  });
  it('filter persistence: revocation aborts old reads and schedules no denied write', async () => {
    const pending = deferred<Setting>(); vi.mocked(apiService.getUserSetting).mockReturnValueOnce(pending.promise); const view = await mount(); const signal = reads()[0][1]?.signal;
    ports.permissions.clients.canView = false; view.rerender(<Clients />); await settle(); await act(async () => pending.resolve(setting('Запрещённый фильтр'))); await settle(); await clock(1000);
    expect(signal?.aborted).toBe(true); expect(reads()).toHaveLength(1); expect(writes()).toHaveLength(0); expect(localStorage.getItem('pp.clients_table_filters.7.v1') ?? '').not.toContain('Запрещённый фильтр');
  });
  it.each(['denied', 'malformed'] as const)('filter persistence: %s read never automatically overwrites server preferences', async kind => {
    if (kind === 'denied') vi.mocked(apiService.getUserSetting).mockRejectedValueOnce(new Error('HTTP 403 - Доступ закрыт'));
    else vi.mocked(apiService.getUserSetting).mockResolvedValueOnce({ key, value: 'not-json' });
    await mount(); await clock(2000); expect(writes()).toHaveLength(0);
  });
  it('filter persistence: a manual edit during loading survives the late current profile', async () => {
    localStorage.setItem('pp.clients_table_filters.7.v1', setting('Локальный').value); const pending = deferred<Setting>(); vi.mocked(apiService.getUserSetting).mockReturnValueOnce(pending.promise); await mount(); edit('Мой ввод', 'Локальный');
    await act(async () => pending.resolve(setting('Серверный'))); await settle(); expect(filterInput('Мой ввод').value).toBe('Мой ввод'); await clock(600);
    expect(writes()).toHaveLength(1); expect(JSON.parse(writes()[0][1]).filters.name).toBe('Мой ввод');
  });
  it('filter persistence: legacy settings load only for a current owner and are not eagerly rewritten', async () => {
    vi.mocked(apiService.getUserSetting).mockImplementation(async name => name === key ? null : { key: name, value: setting('Прежний формат').value });
    await mount(); expect(filterInput('Прежний формат').value).toBe('Прежний формат'); await clock(1500); expect(apiService.getUserSetting).toHaveBeenCalledTimes(2); expect(writes()).toHaveLength(0);
  });
  it('filter persistence: rejected save is handled and a later edit can save normally', async () => {
    vi.mocked(apiService.upsertUserSetting).mockRejectedValueOnce(new Error('Сохранение недоступно')); await mount(); edit('Первый'); await settle(); await clock(600); expect(writes()).toHaveLength(1);
    edit('Повтор', 'Первый'); await settle(); await clock(600); expect(writes()).toHaveLength(2); expect(JSON.parse(writes()[1][1]).filters.name).toBe('Повтор');
  });
  it('filter persistence: an in-flight save is cancelled when the authority changes', async () => {
    const pending = deferred<Awaited<ReturnType<typeof apiService.upsertUserSetting>>>(); vi.mocked(apiService.upsertUserSetting).mockReturnValueOnce(pending.promise); const view = await mount(); edit('Старый допуск'); await settle(); await clock(600); const signal = writes()[0][2]?.signal;
    ports.permissions.clients.viewScope = 'own'; view.rerender(<Clients />); await settle(); expect(signal?.aborted).toBe(true); await act(async () => pending.resolve(setting())); await clock(1000);
    expect(writes()).toHaveLength(1);
  });
});
