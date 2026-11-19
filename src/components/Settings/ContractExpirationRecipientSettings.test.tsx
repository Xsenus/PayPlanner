import { StrictMode } from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ContractExpirationRecipientSettings } from './ContractExpirationRecipientSettings';
import { documentCopyTestSession } from '../../test/documentCopySession';
import type { ContractExpirationRecipientOptionDto, ContractExpirationRecipientSettingsDto } from '../../services/api';

const mocks = vi.hoisted(() => ({
  get: vi.fn(), put: vi.fn(), options: vi.fn(), success: vi.fn(), error: vi.fn(), userId: 7, roleId: 3,
}));
vi.mock('../../services/api', () => ({ apiService: {
  getContractExpirationRecipientSettings: mocks.get,
  updateContractExpirationRecipientSettings: mocks.put,
  getContractExpirationRecipientOptions: mocks.options,
} }));
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { id: mocks.userId, role: { id: mocks.roleId } } }) }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => ({ success: mocks.success, error: mocks.error }) }));

function expectVisible(element: HTMLElement) {
  expect(document.body.contains(element)).toBe(true);
  let current: HTMLElement | null = element;
  while (current) {
    expect(current.hidden).toBe(false);
    const style = window.getComputedStyle(current);
    expect(style.display).not.toBe('none');
    expect(style.visibility).not.toBe('hidden');
    expect(style.visibility).not.toBe('collapse');
    expect(Number.parseFloat(style.opacity)).not.toBe(0);
    current = current.parentElement;
  }
}

const role = { id: 10, name: 'Юристы', isAvailable: true };
const employee = { id: 20, name: 'Анна', isAvailable: true, departmentName: 'Юридический отдел' };
const manager = { id: 21, name: 'Елена', isAvailable: true };
const department = { id: 30, name: 'Юридический отдел', isAvailable: true };
const settings = (overrides: Partial<ContractExpirationRecipientSettingsDto> = {}): ContractExpirationRecipientSettingsDto => ({
  departmentManagersEnabled: false, roleIds: [], userIds: [], departmentManagers: [],
  selectedRoles: [], selectedUsers: [], selectedDepartments: [], ...overrides,
});
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<T>((accept, decline) => { resolve = accept; reject = decline; });
  return { promise, resolve, reject };
}
const page = (items: ContractExpirationRecipientOptionDto[], totalCount = items.length) => ({ items, page: 1, pageSize: 50, totalCount });
async function pick(button: string, name: string) {
  fireEvent.click(screen.getByRole('button', { name: button }));
  fireEvent.click(await screen.findByRole('button', { name }));
}
async function ready() {
  await screen.findByLabelText('Руководители подразделений ответственных');
  await waitFor(() => expect((screen.getByRole('button', { name: 'Сохранить получателей' }) as HTMLButtonElement).disabled).toBe(false));
}

let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => {
  vi.clearAllMocks();
  mocks.userId = 7;
  mocks.roleId = 3;
  session = documentCopyTestSession();
  mocks.get.mockReset().mockResolvedValue(settings());
  mocks.put.mockReset().mockImplementation(async (body) => settings(body));
  mocks.options.mockReset().mockImplementation(async ({ kind }) => page(kind === 'roles' ? [role] : kind === 'departments' ? [department] : [employee, manager]));
});
afterEach(() => { session.restore(); });

describe('contract expiration recipient settings', () => {
  it('loads current settings through the notifications entry without directory preloads and keeps fixed30/14/7 explanatory text', async () => {
    render(<ContractExpirationRecipientSettings canEdit />);
    await ready();
    expect(mocks.get).toHaveBeenCalledOnce();
    expect(mocks.options).not.toHaveBeenCalled();
    expect((screen.getByText(/За 30, 14 и 7 дней/)).textContent).toContain('Ответственные получают напоминания всегда');
    expect(screen.queryByRole('spinbutton')).toBeNull();
  });

  it('combines department managers, roles and named staff in one exact canonical request', async () => {
    render(<ContractExpirationRecipientSettings canEdit />);
    await ready();
    fireEvent.click(screen.getByLabelText('Руководители подразделений ответственных'));
    await pick('Добавить роль', 'Юристы');
    await pick('Добавить сотрудника', 'Елена');
    await pick('Назначить руководителя подразделения', 'Юридический отдел');
    fireEvent.click(await screen.findByRole('button', { name: 'Елена' }));
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить получателей' }));
    await waitFor(() => expect(mocks.put).toHaveBeenCalledOnce());
    expect(mocks.put).toHaveBeenCalledWith({ departmentManagersEnabled: true, roleIds: [10], userIds: [21], departmentManagers: [{ departmentId: 30, managerUserId: 21 }] });
    expect(mocks.success).toHaveBeenCalledOnce();
  });

  it('keeps one selected role and one selected employee after repeated selection', async () => {
    render(<ContractExpirationRecipientSettings canEdit />);
    await ready();
    await pick('Добавить роль', 'Юристы');
    await pick('Добавить роль', 'Юристы');
    await pick('Добавить сотрудника', 'Елена');
    await pick('Добавить сотрудника', 'Елена');
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить получателей' }));
    await waitFor(() => expect(mocks.put).toHaveBeenCalledOnce());
    expect(mocks.put.mock.calls[0][0].roleIds).toEqual([10]);
    expect(mocks.put.mock.calls[0][0].userIds).toEqual([21]);
  });

  it('replaces a department manager without duplicate mappings and retains other selectors', async () => {
    mocks.get.mockResolvedValue(settings({ roleIds: [10], userIds: [20], departmentManagersEnabled: true,
      departmentManagers: [{ departmentId: 30, managerUserId: 20 }], selectedDepartments: [department], selectedRoles: [role], selectedUsers: [employee] }));
    render(<ContractExpirationRecipientSettings canEdit />);
    await ready();
    await pick('Изменить руководителя Юридический отдел', 'Елена');
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить получателей' }));
    await waitFor(() => expect(mocks.put).toHaveBeenCalledOnce());
    expect(mocks.put.mock.calls[0][0]).toEqual({ departmentManagersEnabled: true, roleIds: [10], userIds: [20], departmentManagers: [{ departmentId: 30, managerUserId: 21 }] });
  });

  it('can clear all optional selectors while preserving the always-responsible rule', async () => {
    mocks.get.mockResolvedValue(settings({ roleIds: [10], userIds: [21], departmentManagersEnabled: true,
      departmentManagers: [{ departmentId: 30, managerUserId: 21 }], selectedRoles: [role], selectedUsers: [manager], selectedDepartments: [department] }));
    render(<ContractExpirationRecipientSettings canEdit />);
    await ready();
    fireEvent.click(screen.getByLabelText('Руководители подразделений ответственных'));
    fireEvent.click(screen.getByRole('button', { name: 'Удалить роль Юристы' }));
    fireEvent.click(screen.getByRole('button', { name: 'Удалить сотрудника Елена' }));
    fireEvent.click(screen.getByRole('button', { name: 'Удалить руководителя Юридический отдел' }));
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить получателей' }));
    await waitFor(() => expect(mocks.put).toHaveBeenCalledOnce());
    expect(mocks.put).toHaveBeenCalledWith({ departmentManagersEnabled: false, roleIds: [], userIds: [], departmentManagers: [] });
    expectVisible(screen.getByText(/Ответственные получают напоминания всегда/));
  });

  it('shows unavailable saved IDs and allows explicit cleanup without silently dropping them', async () => {
    mocks.get.mockResolvedValue(settings({ roleIds: [99], userIds: [98] }));
    render(<ContractExpirationRecipientSettings canEdit />);
    await ready();
    expectVisible(screen.getByText('Роль №99 · Недоступен'));
    expectVisible(screen.getByText('Сотрудник №98 · Недоступен'));
    fireEvent.click(screen.getByRole('button', { name: 'Удалить роль Роль №99' }));
    fireEvent.click(screen.getByRole('button', { name: 'Удалить сотрудника Сотрудник №98' }));
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить получателей' }));
    await waitFor(() => expect(mocks.put).toHaveBeenCalledOnce());
    expect(mocks.put.mock.calls[0][0].roleIds).toEqual([]);
    expect(mocks.put.mock.calls[0][0].userIds).toEqual([]);
  });

  it('renders existing choices read-only with notification view permission and performs no write or lookup', async () => {
    mocks.get.mockResolvedValue(settings({ roleIds: [10], selectedRoles: [role] }));
    render(<ContractExpirationRecipientSettings canEdit={false} />);
    expectVisible(await screen.findByText('Юристы'));
    expect((screen.getByRole('button', { name: 'Добавить роль' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Удалить роль Юристы' }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.queryByRole('button', { name: 'Сохранить получателей' })).toBeNull();
    expect(mocks.options).not.toHaveBeenCalled();
    expect(mocks.put).not.toHaveBeenCalled();
  });

  it('keeps settings unavailable after a failed load and restores them only after a successful retry', async () => {
    mocks.get.mockRejectedValueOnce(new Error('HTTP 503 - Настройки временно недоступны'));
    render(<ContractExpirationRecipientSettings canEdit />);
    expect((await screen.findByRole('alert')).textContent).toContain('Настройки временно недоступны');
    expect(screen.queryByLabelText('Руководители подразделений ответственных')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Сохранить получателей' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Обновить получателей' }));
    await ready();
    expect(mocks.put).not.toHaveBeenCalled();
  });

  it('retains the editable draft after a400 rejection and prevents duplicate saves while pending', async () => {
    const pending = deferred<ContractExpirationRecipientSettingsDto>();
    mocks.put.mockReturnValueOnce(pending.promise);
    render(<ContractExpirationRecipientSettings canEdit />);
    await ready();
    fireEvent.click(screen.getByLabelText('Руководители подразделений ответственных'));
    const save = screen.getByRole('button', { name: 'Сохранить получателей' });
    fireEvent.click(save);
    fireEvent.click(save);
    expect(mocks.put).toHaveBeenCalledOnce();
    await act(async () => pending.reject(new Error('HTTP 400 - Выберите действующего сотрудника')));
    expect((screen.getByRole('alert')).textContent).toContain('Выберите действующего сотрудника');
    expect((screen.getByLabelText('Руководители подразделений ответственных') as HTMLInputElement).checked).toBe(true);
    expect((screen.getByRole('button', { name: 'Сохранить получателей' }) as HTMLButtonElement).disabled).toBe(false);
    expect(mocks.success).not.toHaveBeenCalled();
  });

  it('loads bounded pages on demand and resets the page when server search changes', async () => {
    mocks.options.mockImplementation(async ({ page: current }) => page(current === 2 ? [manager] : [employee], 51));
    render(<ContractExpirationRecipientSettings canEdit />);
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Добавить сотрудника' }));
    await screen.findByRole('button', { name: /Анна/ });
    fireEvent.click(screen.getByRole('button', { name: 'Следующие получатели' }));
    await screen.findByRole('button', { name: 'Елена' });
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: '  Ёжик &%_  ' } });
    await waitFor(() => expect(mocks.options.mock.lastCall?.[0]).toEqual({ kind: 'users', search: 'Ёжик &%_', page: 1, pageSize: 50 }));
    expect(mocks.options.mock.calls.every((call) => call[0].pageSize === 50 && call[1].signal instanceof AbortSignal)).toBe(true);
  });

  it('retries a directory failure and closes selection with Escape without changing settings', async () => {
    mocks.options.mockRejectedValueOnce(new Error('HTTP 503 - Список недоступен'));
    render(<ContractExpirationRecipientSettings canEdit />);
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Добавить роль' }));
    expect((await screen.findByRole('alert')).textContent).toContain('Список недоступен');
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку списка' }));
    await screen.findByRole('button', { name: 'Юристы' });
    fireEvent.keyDown(screen.getByRole('searchbox'), { key: 'Escape' });
    expect(screen.queryByRole('region', { name: 'Выбор роли' })).toBeNull();
    expect(mocks.put).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Добавить роль' }));
  });

  it('discards a stale option page after a newer server search', async () => {
    const old = deferred<ReturnType<typeof page>>();
    mocks.options.mockReturnValueOnce(old.promise).mockResolvedValue(page([manager]));
    render(<ContractExpirationRecipientSettings canEdit />);
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Добавить сотрудника' }));
    await waitFor(() => expect(mocks.options).toHaveBeenCalledOnce());
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'Елена' } });
    await screen.findByRole('button', { name: 'Елена' });
    await act(async () => old.resolve(page([employee])));
    expect(screen.queryByRole('button', { name: /Анна/ })).toBeNull();
    expectVisible(screen.getByRole('button', { name: 'Елена' }));
  });

  it('does not accept a late initial read after the role owner changes', async () => {
    const old = deferred<ContractExpirationRecipientSettingsDto>();
    mocks.get.mockReturnValueOnce(old.promise).mockResolvedValue(settings({ roleIds: [10], selectedRoles: [role] }));
    const view = render(<ContractExpirationRecipientSettings canEdit />);
    mocks.roleId++;
    view.rerender(<ContractExpirationRecipientSettings canEdit />);
    await screen.findByText('Юристы');
    await act(async () => old.resolve(settings({ userIds: [21], selectedUsers: [manager] })));
    expect(screen.queryByText('Елена')).toBeNull();
  });

  it.each([
    ['actor', 'success'], ['actor', 'error'],
    ['role', 'success'], ['role', 'error'],
    ['permission', 'success'], ['permission', 'error'],
    ['session', 'success'], ['session', 'error'],
    ['unmount', 'success'], ['unmount', 'error'],
  ] as const)('discards a late save %s owner change with %s outcome', async (change, outcome) => {
    const pending = deferred<ContractExpirationRecipientSettingsDto>();
    mocks.put.mockReturnValueOnce(pending.promise);
    const view = render(<ContractExpirationRecipientSettings canEdit />);
    await ready();
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить получателей' }));
    if (change === 'actor') { mocks.userId++; session.replace('actor'); view.rerender(<ContractExpirationRecipientSettings canEdit />); }
    if (change === 'role') { mocks.roleId++; view.rerender(<ContractExpirationRecipientSettings canEdit />); }
    if (change === 'permission') view.rerender(<ContractExpirationRecipientSettings canEdit={false} />);
    if (change === 'session') session.replace('login');
    if (change === 'unmount') view.unmount();
    await act(async () => {
      if (outcome === 'success') pending.resolve(settings({ userIds: [21], selectedUsers: [manager] }));
      else pending.reject(new Error('HTTP 500 - Чужая старая ошибка'));
    });
    expect(mocks.success).not.toHaveBeenCalled();
    expect(mocks.error).not.toHaveBeenCalled();
    expect(screen.queryByText('Елена')).toBeNull();
    view.unmount();
  });

  it('loads successfully in StrictMode without accepting an expired first lifetime', async () => {
    render(<StrictMode><ContractExpirationRecipientSettings canEdit /></StrictMode>);
    await ready();
    expect(screen.queryByRole('alert')).toBeNull();
    expect(mocks.put).not.toHaveBeenCalled();
  });
});
