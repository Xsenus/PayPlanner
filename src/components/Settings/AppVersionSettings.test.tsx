import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { AppReleaseAdminDto } from '../../services/api';
import { AppVersionSettings } from './AppVersionSettings';

const mocks = vi.hoisted(() => ({
  getAdminAppReleasesPaged: vi.fn(),
  toast: { error: vi.fn(), success: vi.fn() },
  dialog: { confirm: vi.fn() },
}));

vi.mock('../../services/api', async () => {
  const actual = await vi.importActual<typeof import('../../services/api')>('../../services/api');
  return {
    ...actual,
    apiService: {
      ...actual.apiService,
      getAdminAppReleasesPaged: mocks.getAdminAppReleasesPaged,
    },
  };
});

vi.mock('../../hooks/useToast', () => ({
  useToast: () => mocks.toast,
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => mocks.dialog,
}));

const release: AppReleaseAdminDto = {
  id: 778,
  releaseId: '2026-08-04-extremely-long-release-identifier-for-mobile-layout-regression',
  version: '1.13.778',
  releasedAt: '2026-08-04T04:45:00Z',
  title: 'Подключение Яндекс.Диска можно безопасно восстановить',
  summary: 'Описание обновления',
  items: [{ type: 'fixed', text: 'Исправлено восстановление подключения.' }],
  isActive: true,
  source: 'agent',
  createdAt: '2026-08-04T04:45:00Z',
  updatedAt: '2026-08-04T04:45:00Z',
};

describe('AppVersionSettings', () => {
  beforeEach(() => {
    mocks.getAdminAppReleasesPaged.mockReset();
    mocks.getAdminAppReleasesPaged.mockResolvedValue({
      items: [release],
      total: 855,
      page: 1,
      pageSize: 20,
    });
    mocks.toast.error.mockReset();
    mocks.toast.success.mockReset();
    mocks.dialog.confirm.mockReset();
  });

  it('не позволяет колонкам и длинному идентификатору расширять мобильную страницу', async () => {
    render(<AppVersionSettings />);

    await waitFor(() => expect(mocks.getAdminAppReleasesPaged).toHaveBeenCalledTimes(1));

    expect(mocks.getAdminAppReleasesPaged).toHaveBeenCalledWith({ page: 1, pageSize: 20, search: '' });
    expect(await screen.findByText('855')).toBeTruthy();

    expect(screen.getByTestId('app-release-settings').className).toContain('grid-cols-[minmax(0,1fr)]');
    expect(screen.getByTestId('app-release-settings').className).toContain('xl:grid-cols-[320px_minmax(0,1fr)]');
    expect(screen.getByTestId('app-release-list').className).toContain('min-w-0');
    expect(screen.getByTestId('app-release-list').textContent).toContain('Всего релизов');
    expect(screen.getByTestId('app-release-editor').className).toContain('min-w-0');
    expect(screen.getByText(release.releaseId).className).toContain('break-all');
  });

  it('запрашивает следующую страницу по кнопке пагинации', async () => {
    render(<AppVersionSettings />);
    await waitFor(() => expect(mocks.getAdminAppReleasesPaged).toHaveBeenCalledTimes(1));

    fireEvent.click(await screen.findByRole('button', { name: 'Вперёд' }));

    await waitFor(() =>
      expect(mocks.getAdminAppReleasesPaged).toHaveBeenLastCalledWith({
        page: 2,
        pageSize: 20,
        search: '',
      }),
    );
  });
});
