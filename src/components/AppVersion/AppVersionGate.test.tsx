import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService, type AppVersionLatestDto } from '../../services/api';
import { resetAppVersionSessionDismissalsForTests } from './AppVersionDismissals';
import { AppVersionGate } from './AppVersionGate';

vi.mock('../../services/api', () => ({
  apiService: {
    getLatestAppVersion: vi.fn(),
    getAppVersionHistory: vi.fn(),
    markAppVersionSeen: vi.fn(),
  },
}));

const release = {
  id: 1,
  releaseId: '2026-05-21-accounting-v2',
  version: '1.12.0',
  releasedAt: '2026-05-21T00:00:00',
  title: 'Обновление бухгалтерии',
  summary: 'Краткое описание релиза',
  items: [
    { type: 'new', text: 'Добавлен новый блок.' },
    { type: 'improved', text: 'Ускорена загрузка.' },
    { type: 'fixed', text: 'Исправлен пересчёт.' },
    { type: 'important', text: 'Закрытые периоды не пересчитываются.' },
  ],
};

const previousRelease = {
  ...release,
  id: 2,
  releaseId: '2026-05-20-previous',
  version: '1.11.0',
  releasedAt: '2026-05-20T14:30:00',
  title: 'Предыдущее обновление',
  summary: 'Описание прошлого релиза',
  items: [
    { type: 'fixed', text: 'Исправлена старая ошибка.' },
  ],
};

function latest(overrides?: Partial<AppVersionLatestDto>): AppVersionLatestDto {
  return {
    currentVersion: '1.12.0',
    latestRelease: release,
    seenByCurrentUser: false,
    ...overrides,
  };
}

beforeEach(() => {
  window.localStorage.clear();
  window.sessionStorage.clear();
  vi.mocked(apiService.getLatestAppVersion).mockResolvedValue(latest());
  vi.mocked(apiService.getAppVersionHistory).mockResolvedValue([release, previousRelease]);
  vi.mocked(apiService.markAppVersionSeen).mockResolvedValue(undefined);
  resetAppVersionSessionDismissalsForTests();
});

afterEach(() => {
  vi.clearAllMocks();
});

describe('AppVersionGate', () => {
  it('shows release notes automatically when user has not seen the release', async () => {
    render(<AppVersionGate userId={7} manualOpenSignal={0} onManualOpenHandled={vi.fn()} />);

    expect(await screen.findByRole('dialog')).toBeTruthy();
    expect(screen.getByTestId('app-version-dialog').className).toContain('max-w-[1080px]');
    expect(await screen.findByTestId('app-version-history')).toBeTruthy();
    expect(screen.getByTestId('app-version-mobile-history-toggle')).toBeTruthy();
    expect(screen.getByTestId('app-version-current-badge').className).toContain('absolute');
    expect(screen.getByLabelText('Текущее обновление')).toBeTruthy();
    expect(screen.getAllByText(/Версия 1.12.0/).length).toBeGreaterThan(0);
    expect(screen.getByText('Добавлен новый блок.')).toBeTruthy();
  });

  it('marks release as seen on close and does not show it again in the same session', async () => {
    const { unmount } = render(<AppVersionGate userId={7} manualOpenSignal={0} onManualOpenHandled={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Понятно' }));

    await waitFor(() => expect(apiService.markAppVersionSeen).toHaveBeenCalledWith(release.releaseId));
    unmount();
    render(<AppVersionGate userId={7} manualOpenSignal={0} onManualOpenHandled={vi.fn()} />);

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('does not show dismissed release again after a page refresh while server still reports it unseen', async () => {
    const { unmount } = render(<AppVersionGate userId={7} manualOpenSignal={0} onManualOpenHandled={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Понятно' }));

    await waitFor(() => expect(apiService.markAppVersionSeen).toHaveBeenCalledWith(release.releaseId));
    unmount();
    resetAppVersionSessionDismissalsForTests();

    render(<AppVersionGate userId={7} manualOpenSignal={0} onManualOpenHandled={vi.fn()} />);

    await waitFor(() => expect(apiService.getLatestAppVersion).toHaveBeenCalledTimes(2));
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('shows automatically again when a new releaseId appears', async () => {
    const { unmount } = render(<AppVersionGate userId={7} manualOpenSignal={0} onManualOpenHandled={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Понятно' }));
    await waitFor(() => expect(apiService.markAppVersionSeen).toHaveBeenCalledTimes(1));
    unmount();

    vi.mocked(apiService.getLatestAppVersion).mockResolvedValue(latest({
      latestRelease: { ...release, releaseId: '2026-05-22-next' },
      seenByCurrentUser: false,
    }));
    render(<AppVersionGate userId={7} manualOpenSignal={0} onManualOpenHandled={vi.fn()} />);

    expect(await screen.findByRole('dialog')).toBeTruthy();
  });

  it('opens manually even when release was already seen', async () => {
    vi.mocked(apiService.getLatestAppVersion).mockResolvedValue(latest({ seenByCurrentUser: true }));
    const onManualOpenHandled = vi.fn();
    const { rerender } = render(<AppVersionGate userId={7} manualOpenSignal={0} onManualOpenHandled={onManualOpenHandled} />);

    await waitFor(() => expect(apiService.getLatestAppVersion).toHaveBeenCalledTimes(1));
    expect(screen.queryByRole('dialog')).toBeNull();

    rerender(<AppVersionGate userId={7} manualOpenSignal={1} onManualOpenHandled={onManualOpenHandled} />);

    expect(await screen.findByRole('dialog')).toBeTruthy();
    expect(onManualOpenHandled).toHaveBeenCalledTimes(1);
  });

  it('keeps the app usable when latest version API fails on startup', async () => {
    vi.mocked(apiService.getLatestAppVersion).mockRejectedValueOnce(new Error('network'));

    render(<AppVersionGate userId={7} manualOpenSignal={0} onManualOpenHandled={vi.fn()} />);

    await waitFor(() => expect(apiService.getLatestAppVersion).toHaveBeenCalledTimes(1));
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('opens history, shows selected past release, and returns to current release', async () => {
    render(<AppVersionGate userId={7} manualOpenSignal={0} onManualOpenHandled={vi.fn()} />);

    expect(await screen.findByRole('dialog')).toBeTruthy();
    expect(await screen.findByText('Предыдущее обновление')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Версия 1.11.0/ }));

    expect(screen.getByText('Описание прошлого релиза')).toBeTruthy();
    expect(screen.getByText('Исправлена старая ошибка.')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /Показать текущее/ }));

    expect(screen.getByText(release.summary)).toBeTruthy();
  });
});
