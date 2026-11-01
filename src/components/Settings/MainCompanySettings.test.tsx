import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MainCompanySettings } from './MainCompanySettings';
import { documentCopyTestSession } from '../../test/documentCopySession';
import { StrictMode } from 'react';
import { useLegalEntities } from '../../hooks/useLegalEntities';

const mocks = vi.hoisted(() => ({
  get: vi.fn(), put: vi.fn(), refresh: vi.fn(), error: vi.fn(), success: vi.fn(), userId: 3, roleId: 4, admin: true,
}));
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { id: mocks.userId, role: { id: mocks.roleId } }, isAdmin: () => mocks.admin }) }));
vi.mock('../../contexts/TabContext', () => ({ useTabNavigation: () => ({ setActiveTab: vi.fn() }) }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => ({ error: mocks.error, success: mocks.success }) }));
vi.mock('../../hooks/useLegalEntities', () => ({ useLegalEntities: vi.fn(() => ({ legalEntities: [], error: null, refresh: mocks.refresh })) }));
vi.mock('../../services/api', () => ({ apiService: { getMainCompanySettings: mocks.get, updateMainCompanySettings: mocks.put } }));

describe('MainCompanySettings snapshot timing', () => {
  it('directory caller: own-company settings uses its explicit lookup scope only while admitted', async () => {
    const view = render(<MainCompanySettings />); await waitFor(() => expect(mocks.get).toHaveBeenCalled());
    expect(vi.mocked(useLegalEntities).mock.lastCall?.[0]?.ownerScope).toBe('main-company-settings'); const count = vi.mocked(useLegalEntities).mock.calls.length;
    mocks.admin = false; view.rerender(<MainCompanySettings />); expect(vi.mocked(useLegalEntities).mock.calls).toHaveLength(count);
  });
  let session: ReturnType<typeof documentCopyTestSession>;
  beforeEach(() => {
    vi.clearAllMocks();
    session = documentCopyTestSession();
    Object.assign(mocks, { userId: 3, roleId: 4, admin: true });
    mocks.get.mockReset().mockResolvedValue({ companies: [], defaultCompanyId: null, settingsVersion: 'A'.repeat(64) });
    mocks.put.mockReset().mockResolvedValue({ companies: [], defaultCompanyId: null, sellerSnapshotTiming: 'on-create', settingsVersion: 'B'.repeat(64) });
    mocks.refresh.mockResolvedValue(undefined);
  });
  afterEach(() => session.restore());

  it('keeps first-file when timing is absent and makes a timing-only change saveable', async () => {
    render(<MainCompanySettings />);
    const select = (await screen.findByLabelText('Когда фиксировать реквизиты и изображения компании в новом документе')) as HTMLSelectElement;
    await waitFor(() => expect(select.matches(':disabled')).toBe(false));
    expect(select.value).toBe('first-file');
    expect(screen.getByRole('button', { name: 'Сохранить' }).matches(':disabled')).toBe(true);

    fireEvent.change(select, { target: { value: 'on-create' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(mocks.put).toHaveBeenCalledWith({ expectedVersion: 'A'.repeat(64), companyIds: [], defaultCompanyId: null, sellerSnapshotTiming: 'on-create' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Сохранить' }).matches(':disabled')).toBe(true));
    const notice = screen.getByText(/Уже созданные документы и их снимки не меняются/);
    expect(notice.isConnected).toBe(true);
    for (let element: HTMLElement | null = notice; element; element = element.parentElement) {
      const style = window.getComputedStyle(element);
      expect(element.hidden || style.display === 'none' || ['hidden', 'collapse'].includes(style.visibility) || style.opacity === '0').toBe(false);
    }
  });

  it('preserves a rejected choice for an explicit retry and never reports it saved', async () => {
    mocks.put.mockRejectedValue(new Error('Нет права сохранять настройку'));
    render(<MainCompanySettings />);
    const select = (await screen.findByLabelText('Когда фиксировать реквизиты и изображения компании в новом документе')) as HTMLSelectElement;
    await waitFor(() => expect(select.matches(':disabled')).toBe(false));
    fireEvent.change(select, { target: { value: 'on-create' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(mocks.error).toHaveBeenCalled());
    expect(select.value).toBe('on-create');
    expect(screen.getByRole('button', { name: 'Сохранить' }).matches(':disabled')).toBe(false);
    expect(mocks.put).toHaveBeenCalledTimes(1);
    expect(mocks.success).not.toHaveBeenCalled();
  });

  it('keeps the loaded version and the draft after a conflict until explicit reload', async () => {
    mocks.put.mockRejectedValue(new Error('HTTP 409 Conflict - Настройки изменились'));
    render(<MainCompanySettings />);
    const select = await screen.findByLabelText('Когда фиксировать реквизиты и изображения компании в новом документе');
    await waitFor(() => expect(select.matches(':disabled')).toBe(false));
    fireEvent.change(select, { target: { value: 'on-create' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(mocks.error).toHaveBeenCalledOnce());
    expect(mocks.get).toHaveBeenCalledOnce();
    expect(mocks.put.mock.calls[0][0].expectedVersion).toBe('A'.repeat(64));
    expect((select as HTMLSelectElement).value).toBe('on-create');
    expect(mocks.success).not.toHaveBeenCalled();
    mocks.get.mockResolvedValue({ companies: [], defaultCompanyId: null, settingsVersion: 'C'.repeat(64), sellerSnapshotTiming: 'first-file' });
    fireEvent.click(screen.getByRole('button', { name: 'Загрузить сохранённые настройки' }));
    await waitFor(() => expect((select as HTMLSelectElement).value).toBe('first-file'));
    fireEvent.change(select, { target: { value: 'on-create' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(2));
    expect(mocks.put.mock.calls[1][0].expectedVersion).toBe('C'.repeat(64));
  });

  it.each([undefined, 'bad'])('refuses to save a response without a usable version: %s', async settingsVersion => {
    mocks.get.mockResolvedValue({ companies: [], settingsVersion });
    render(<MainCompanySettings />);
    await screen.findByText(/Не удалось получить актуальную версию настроек/);
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    expect(mocks.put).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Сохранить' }).matches(':disabled')).toBe(true);
  });

  it.each(['user', 'role', 'admin', 'login', 'actor', 'logout'])('discards a late save result after %s change', async kind => {
    let resolve!: (value: object) => void;
    mocks.put.mockImplementationOnce(() => new Promise<object>(yes => { resolve = yes; }));
    const view = render(<MainCompanySettings />);
    const select = await screen.findByLabelText('Когда фиксировать реквизиты и изображения компании в новом документе');
    await waitFor(() => expect(select.matches(':disabled')).toBe(false));
    fireEvent.change(select, { target: { value: 'on-create' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    expect(mocks.put).toHaveBeenCalledOnce();
    if (kind === 'user') mocks.userId++;
    else if (kind === 'role') mocks.roleId++;
    else if (kind === 'admin') mocks.admin = false;
    else session.replace(kind as 'login' | 'actor' | 'logout');
    mocks.get.mockResolvedValue({ companies: [], settingsVersion: 'C'.repeat(64), sellerSnapshotTiming: 'first-file' });
    view.rerender(<MainCompanySettings />);
    await act(async () => resolve({ companies: [], settingsVersion: 'B'.repeat(64), sellerSnapshotTiming: 'on-create' }));
    expect(mocks.success).not.toHaveBeenCalled(); expect(mocks.error).not.toHaveBeenCalled(); expect(mocks.refresh).not.toHaveBeenCalled();
    if (kind === 'admin' || kind === 'logout') expect(screen.queryByRole('button', { name: 'Сохранить' })).toBeNull();
    else await waitFor(() => expect((screen.getByLabelText('Когда фиксировать реквизиты и изображения компании в новом документе') as HTMLSelectElement).value).toBe('first-file'));
  });

  it.each(['success', 'failure'])('does not adopt an old load %s after an account change', async outcome => {
    let resolve!: (value: object) => void; let reject!: (error: Error) => void;
    mocks.get.mockImplementationOnce(() => new Promise<object>((yes, no) => { resolve = yes; reject = no; }));
    const view = render(<MainCompanySettings />);
    expect(mocks.get).toHaveBeenCalledOnce();
    mocks.userId++;
    mocks.get.mockResolvedValue({ companies: [], settingsVersion: 'C'.repeat(64), sellerSnapshotTiming: 'on-create' });
    view.rerender(<MainCompanySettings />);
    await waitFor(() => expect((screen.getByLabelText('Когда фиксировать реквизиты и изображения компании в новом документе') as HTMLSelectElement).value).toBe('on-create'));
    await act(async () => {
      if (outcome === 'success') resolve({ companies: [], settingsVersion: 'A'.repeat(64), sellerSnapshotTiming: 'first-file' });
      else reject(new Error('Old account load failed'));
    });
    expect((screen.getByLabelText('Когда фиксировать реквизиты и изображения компании в новом документе') as HTMLSelectElement).value).toBe('on-create');
    expect(screen.queryByText('Old account load failed')).toBeNull();
  });

  it('blocks duplicate submission and captures the returned version for the next edit', async () => {
    let resolve!: (value: object) => void;
    mocks.put.mockImplementationOnce(() => new Promise<object>(yes => { resolve = yes; }));
    render(<MainCompanySettings />);
    const select = await screen.findByLabelText('Когда фиксировать реквизиты и изображения компании в новом документе');
    await waitFor(() => expect(select.matches(':disabled')).toBe(false));
    fireEvent.change(select, { target: { value: 'on-create' } });
    const save = screen.getByRole('button', { name: 'Сохранить' });
    fireEvent.click(save); fireEvent.click(save);
    expect(mocks.put).toHaveBeenCalledOnce();
    await act(async () => resolve({ companies: [], settingsVersion: 'B'.repeat(64), sellerSnapshotTiming: 'on-create' }));
    fireEvent.change(select, { target: { value: 'first-file' } });
    fireEvent.click(save);
    await waitFor(() => expect(mocks.put).toHaveBeenCalledTimes(2));
    expect(mocks.put.mock.calls[1][0].expectedVersion).toBe('B'.repeat(64));
    expect(mocks.get).toHaveBeenCalledOnce();
  });

  it('loads and saves with React strict lifecycle checks', async () => {
    render(<StrictMode><MainCompanySettings /></StrictMode>);
    const select = await screen.findByLabelText('Когда фиксировать реквизиты и изображения компании в новом документе');
    await waitFor(() => expect(select.matches(':disabled')).toBe(false));
    fireEvent.change(select, { target: { value: 'on-create' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(mocks.success).toHaveBeenCalledOnce());
    expect(mocks.put).toHaveBeenCalledOnce();
  });

  it('does not load or send a write without the administrator role', () => {
    mocks.admin = false;
    render(<MainCompanySettings />);
    expect(mocks.get).not.toHaveBeenCalled(); expect(mocks.put).not.toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: 'Сохранить' })).toBeNull();
  });
});
