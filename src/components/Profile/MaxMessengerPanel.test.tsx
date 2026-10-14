import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { StrictMode } from 'react';
import { MaxMessengerPanel } from './MaxMessengerPanel';

const api = vi.hoisted(() => ({ getEmployeeMaxLinkStatus: vi.fn(), createEmployeeMaxLink: vi.fn(), deleteEmployeeMaxLink: vi.fn() }));
const confirm = vi.hoisted(() => vi.fn());
vi.mock('../../services/api', () => ({ apiService: api }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm }) }));

const status = (value = 'unlinked', options = {}) => ({ status: value, channelReady: true, linkId: null, expiresAt: null, verifiedAt: null, ...options });
const link = () => ({ linkId: 'own-link', deepLinkUrl: 'https://max.ru/TestBot?start=one-time-token', expiresAt: new Date(Date.now() + 900_000).toISOString() });

describe('MaxMessengerPanel', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    api.getEmployeeMaxLinkStatus.mockResolvedValue(status());
    api.createEmployeeMaxLink.mockImplementation(async () => link());
    api.deleteEmployeeMaxLink.mockResolvedValue(undefined);
    confirm.mockResolvedValue(true);
  });
  afterEach(() => { cleanup(); vi.useRealTimers(); });

  it('reads only the personal status on mount and creates a link only on click', async () => {
    render(<MaxMessengerPanel />);
    await screen.findByText('Не подключён');
    expect(api.createEmployeeMaxLink).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Подключить MAX' }));
    const anchor = await screen.findByRole('link', { name: 'Открыть бота MAX' });
    expect(anchor.getAttribute('href')).toBe('https://max.ru/TestBot?start=one-time-token');
    expect(anchor.getAttribute('rel')).toBe('noopener noreferrer');
    expect(anchor.getAttribute('referrerpolicy')).toBe('no-referrer');
    expect(api.createEmployeeMaxLink).toHaveBeenCalledTimes(1);
    expect(screen.getByText('Ожидается подтверждение в MAX')).toBeTruthy();
  });

  it('prevents repeated creation while the first request is pending', async () => {
    let finish!: (value: ReturnType<typeof link>) => void;
    api.createEmployeeMaxLink.mockImplementation(() => new Promise(resolve => { finish = resolve; }));
    render(<MaxMessengerPanel />);
    await screen.findByText('Не подключён');
    const button = screen.getByRole('button', { name: 'Подключить MAX' });
    fireEvent.click(button);
    fireEvent.click(button);
    expect(api.createEmployeeMaxLink).toHaveBeenCalledTimes(1);
    await act(async () => { finish(link()); });
    expect(screen.getByRole('link', { name: 'Открыть бота MAX' })).toBeTruthy();
  });

  it('refreshes verification and discards the one-time link', async () => {
    render(<MaxMessengerPanel />);
    await screen.findByText('Не подключён');
    fireEvent.click(screen.getByRole('button', { name: 'Подключить MAX' }));
    await screen.findByRole('link', { name: 'Открыть бота MAX' });
    api.getEmployeeMaxLinkStatus.mockResolvedValue(status('verified', { linkId: 'own-link', verifiedAt: new Date().toISOString() }));
    fireEvent.click(screen.getByRole('button', { name: 'Обновить статус' }));
    await screen.findByText('Подключён');
    expect(screen.queryByRole('link', { name: 'Открыть бота MAX' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Отключить MAX' })).toBeTruthy();
  });

  it('does not reuse a link replaced in another tab', async () => {
    render(<MaxMessengerPanel />);
    await screen.findByText('Не подключён');
    fireEvent.click(screen.getByRole('button', { name: 'Подключить MAX' }));
    await screen.findByRole('link', { name: 'Открыть бота MAX' });
    api.getEmployeeMaxLinkStatus.mockResolvedValue(status('pending', { linkId: 'replacement', expiresAt: link().expiresAt }));
    fireEvent.click(screen.getByRole('button', { name: 'Обновить статус' }));
    await waitFor(() => expect(screen.queryByRole('link', { name: 'Открыть бота MAX' })).toBeNull());
    expect(screen.getByText('Чтобы снова открыть бота, создайте новую ссылку.')).toBeTruthy();
  });

  it('disables creation when the organizational channel is unavailable', async () => {
    api.getEmployeeMaxLinkStatus.mockResolvedValue(status('unlinked', { channelReady: false }));
    render(<MaxMessengerPanel />);
    await screen.findByText('Не подключён');
    const button = screen.getByRole('button', { name: 'Подключить MAX' }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    fireEvent.click(button);
    expect(api.createEmployeeMaxLink).not.toHaveBeenCalled();
    expect(screen.getByText('Канал MAX отключён или требует настройки администратором.')).toBeTruthy();
  });

  it.each(['javascript:alert(1)', 'https://evil.test/bot', 'http://max.ru/TestBot', 'https://user:password@max.ru/TestBot'])('rejects an unsafe bot link: %s', async deepLinkUrl => {
    api.createEmployeeMaxLink.mockResolvedValue({ ...link(), deepLinkUrl });
    render(<MaxMessengerPanel />);
    await screen.findByText('Не подключён');
    fireEvent.click(screen.getByRole('button', { name: 'Подключить MAX' }));
    await screen.findByRole('alert');
    expect(screen.queryByRole('link', { name: 'Открыть бота MAX' })).toBeNull();
  });

  it('shows request errors and allows retry without guessing the status', async () => {
    api.getEmployeeMaxLinkStatus.mockRejectedValueOnce(new Error('Нет связи'));
    render(<MaxMessengerPanel />);
    await screen.findByRole('alert');
    expect(screen.queryByRole('button', { name: 'Подключить MAX' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Обновить статус' }));
    await screen.findByText('Не подключён');
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it.each(['not-a-date', '2000-01-01T00:00:00Z'])('does not expose an invalid or expired link: %s', async expiresAt => {
    api.createEmployeeMaxLink.mockResolvedValue({ ...link(), expiresAt });
    render(<MaxMessengerPanel />);
    await screen.findByText('Не подключён');
    fireEvent.click(screen.getByRole('button', { name: 'Подключить MAX' }));
    await screen.findByRole('alert');
    expect(screen.queryByRole('link', { name: 'Открыть бота MAX' })).toBeNull();
  });

  it('requires confirmation before disconnecting and preserves the status on cancellation', async () => {
    api.getEmployeeMaxLinkStatus.mockResolvedValue(status('verified', { linkId: 'own-link' }));
    confirm.mockResolvedValueOnce(false);
    render(<MaxMessengerPanel />);
    await screen.findByText('Подключён');
    fireEvent.click(screen.getByRole('button', { name: 'Отключить MAX' }));
    await waitFor(() => expect(confirm).toHaveBeenCalledTimes(1));
    expect(api.deleteEmployeeMaxLink).not.toHaveBeenCalled();
    api.getEmployeeMaxLinkStatus.mockResolvedValue(status('opted-out', { linkId: 'own-link' }));
    fireEvent.click(screen.getByRole('button', { name: 'Отключить MAX' }));
    await screen.findByText('Отключён');
    expect(api.deleteEmployeeMaxLink).toHaveBeenCalledTimes(1);
  });

  it('removes the link at expiry and reads the server status once', async () => {
    vi.useFakeTimers();
    render(<MaxMessengerPanel />);
    await act(async () => {});
    api.createEmployeeMaxLink.mockResolvedValue({ ...link(), expiresAt: new Date(Date.now() + 1000).toISOString() });
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Подключить MAX' })); });
    expect(screen.getByRole('link', { name: 'Открыть бота MAX' })).toBeTruthy();
    api.getEmployeeMaxLinkStatus.mockResolvedValue(status('expired', { linkId: 'own-link', expiresAt: new Date(Date.now()).toISOString() }));
    await act(async () => { await vi.advanceTimersByTimeAsync(1001); });
    expect(screen.queryByRole('link', { name: 'Открыть бота MAX' })).toBeNull();
    expect(screen.getByText('Срок ссылки истёк')).toBeTruthy();
    expect(api.getEmployeeMaxLinkStatus).toHaveBeenCalledTimes(2);
  });

  it('does not restore a one-time URL after the panel is remounted', async () => {
    const view = render(<MaxMessengerPanel />);
    await screen.findByText('Не подключён');
    fireEvent.click(screen.getByRole('button', { name: 'Подключить MAX' }));
    await screen.findByRole('link', { name: 'Открыть бота MAX' });
    view.unmount();
    api.getEmployeeMaxLinkStatus.mockResolvedValue(status('pending', { linkId: 'own-link', expiresAt: link().expiresAt }));
    render(<MaxMessengerPanel />);
    await screen.findByText('Ожидается подтверждение в MAX');
    expect(screen.queryByRole('link', { name: 'Открыть бота MAX' })).toBeNull();
  });

  it('ignores stale initial responses in StrictMode and keeps loading the current request', async () => {
    let resolveOld!: (value: ReturnType<typeof status>) => void;
    let resolveCurrent!: (value: ReturnType<typeof status>) => void;
    api.getEmployeeMaxLinkStatus.mockImplementationOnce(() => new Promise(resolve => { resolveOld = resolve; }))
      .mockImplementationOnce(() => new Promise(resolve => { resolveCurrent = resolve; }));
    render(<StrictMode><MaxMessengerPanel /></StrictMode>);
    await act(async () => { resolveOld(status('verified', { linkId: 'stale' })); });
    expect(screen.getByText('Загрузка…')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Обновить статус' }) as HTMLButtonElement).disabled).toBe(true);
    await act(async () => { resolveCurrent(status()); });
    expect(screen.getByText('Не подключён')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Отключить MAX' })).toBeNull();
  });
});
