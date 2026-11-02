import { documentCopyTestSession } from '../../test/documentCopySession';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi, afterEach } from 'vitest';
let sharedTimezoneTestSession: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { sharedTimezoneTestSession = documentCopyTestSession(); });
afterEach(() => sharedTimezoneTestSession.restore());

import { YandexDisk } from './YandexDisk';

const mocks = vi.hoisted(() => ({
  toastSuccessMock: vi.fn(),
  toastErrorMock: vi.fn(),
  confirmMock: vi.fn(),
  getYandexDiskConnectionsMock: vi.fn(),
  upsertYandexDiskConnectionMock: vi.fn(),
  startYandexDiskAuthMock: vi.fn(),
  getYandexDiskAuthStatusMock: vi.fn(),
  deleteYandexDiskConnectionMock: vi.fn(),
  testYandexDiskConnectionByIdMock: vi.fn(),
  listYandexDiskResourcesMock: vi.fn(),
  createYandexDiskFolderMock: vi.fn(),
  copyYandexDiskResourceMock: vi.fn(),
  moveYandexDiskResourceMock: vi.fn(),
  deleteYandexDiskResourceMock: vi.fn(),
  uploadYandexDiskFileMock: vi.fn(),
  downloadYandexDiskFileMock: vi.fn(),
  getYandexDiskTextFileMock: vi.fn(),
  saveYandexDiskTextFileMock: vi.fn(),
  getYandexDiskCrmSyncSettingsMock: vi.fn(),
  getYandexDiskCrmSyncPreviewMock: vi.fn(),
  getYandexDiskCrmSyncStatusMock: vi.fn(),
  updateYandexDiskCrmSyncSettingsMock: vi.fn(),
  getUserSettingMock: vi.fn(),
}));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: { id: 1, role: { id: 1, name: 'admin' } },
  }),
}));

vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: () => ({
    yandexDisk: {
      canView: true,
      canCreate: true,
      canEdit: true,
      canDelete: true,
      canExport: true,
      viewScope: 'all',
    },
  }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({
    success: mocks.toastSuccessMock,
    error: mocks.toastErrorMock,
  }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({
    confirm: mocks.confirmMock,
    alert: vi.fn(),
  }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getYandexDiskConnections: mocks.getYandexDiskConnectionsMock,
    upsertYandexDiskConnection: mocks.upsertYandexDiskConnectionMock,
    startYandexDiskAuth: mocks.startYandexDiskAuthMock,
    getYandexDiskAuthStatus: mocks.getYandexDiskAuthStatusMock,
    deleteYandexDiskConnection: mocks.deleteYandexDiskConnectionMock,
    testYandexDiskConnectionById: mocks.testYandexDiskConnectionByIdMock,
    listYandexDiskResources: mocks.listYandexDiskResourcesMock,
    createYandexDiskFolder: mocks.createYandexDiskFolderMock,
    copyYandexDiskResource: mocks.copyYandexDiskResourceMock,
    moveYandexDiskResource: mocks.moveYandexDiskResourceMock,
    deleteYandexDiskResource: mocks.deleteYandexDiskResourceMock,
    uploadYandexDiskFile: mocks.uploadYandexDiskFileMock,
    downloadYandexDiskFile: mocks.downloadYandexDiskFileMock,
    getYandexDiskTextFile: mocks.getYandexDiskTextFileMock,
    saveYandexDiskTextFile: mocks.saveYandexDiskTextFileMock,
    getYandexDiskCrmSyncSettings: mocks.getYandexDiskCrmSyncSettingsMock,
    getYandexDiskCrmSyncPreview: mocks.getYandexDiskCrmSyncPreviewMock,
    getYandexDiskCrmSyncStatus: mocks.getYandexDiskCrmSyncStatusMock,
    updateYandexDiskCrmSyncSettings: mocks.updateYandexDiskCrmSyncSettingsMock,
    getUserSetting: mocks.getUserSettingMock,
  },
}));

function buildConnection(overrides = {}) {
  return {
    id: 'ya-main',
    name: 'Я.Диск основной',
    isEnabled: true,
    isConfigured: true,
    requiresReconnect: false,
    rootPath: 'disk:/PayPlanner',
    tokenPreview: 'ya-t...1234',
    updatedAt: '2026-06-01T11:00:00+07:00',
    ...overrides,
  };
}

function buildSyncSettings() {
  return {
    enabled: false,
    syncInvoices: true,
    syncActs: true,
    syncContracts: true,
    syncCaseDocuments: true,
    syncTaskFiles: false,
    overwriteExisting: false,
    conflictResolutionMode: 'yandexDiskMaster',
    conflictResolutionModeText: 'Я.Диск главный',
    folderTemplate: 'CRM/{clientName}/{caseNumber}/{documentType}',
    fileNameTemplate: '{documentDate}-{documentNumber}-{documentTitle}',
    updatedAt: '2026-06-01T11:00:00+07:00',
    availableTokens: ['clientName', 'caseNumber'],
  };
}

describe('YandexDisk', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.confirmMock.mockResolvedValue(true);
    mocks.getUserSettingMock.mockResolvedValue({ key: 'profile.timeZoneId', value: 'Europe/Samara' });
    mocks.getYandexDiskConnectionsMock.mockResolvedValue({
      connections: [
        buildConnection(),
        buildConnection({
          id: 'ya-archive',
          name: 'Я.Диск архив',
          rootPath: 'disk:/Archive',
          tokenPreview: 'arch...5678',
        }),
      ],
    });
    mocks.upsertYandexDiskConnectionMock.mockResolvedValue(buildConnection({ id: 'ya-new', name: 'Я.Диск новый' }));
    mocks.startYandexDiskAuthMock.mockResolvedValue({
      state: 'oauth-state',
      authUrl: 'https://oauth.yandex.ru/authorize?state=oauth-state',
      pollIntervalMs: 2000,
    });
    mocks.getYandexDiskAuthStatusMock.mockResolvedValue({ state: 'oauth-state', status: 'pending', completed: false });
    mocks.deleteYandexDiskConnectionMock.mockResolvedValue(undefined);
    mocks.testYandexDiskConnectionByIdMock.mockResolvedValue({
      success: true,
      rootPath: 'disk:/PayPlanner',
      testFolderPath: '/_payplanner-test',
      checkedAt: '2026-06-01T12:00:00+07:00',
      steps: [
        { key: 'list', name: 'Проверка доступа', success: true, message: 'Диск доступен.' },
        { key: 'upload', name: 'Проверка загрузки', success: true, message: 'Файл загружен.' },
      ],
    });
    mocks.listYandexDiskResourcesMock.mockImplementation(() => Promise.resolve({
      current: { name: 'PayPlanner', path: '/', fullPath: 'disk:/PayPlanner', type: 'dir' },
      items: [
        { name: 'docs', path: '/docs', fullPath: 'disk:/PayPlanner/docs', type: 'dir' },
        { name: 'readme.txt', path: '/readme.txt', fullPath: 'disk:/PayPlanner/readme.txt', type: 'file', size: 12, modified: '2026-06-01T12:00:00+07:00' },
      ],
      limit: 100,
      offset: 0,
      total: 2,
    }));
    mocks.createYandexDiskFolderMock.mockResolvedValue({ success: true, path: '/new-folder', fullPath: 'disk:/PayPlanner/new-folder' });
    mocks.copyYandexDiskResourceMock.mockResolvedValue({ success: true, path: '/copy-readme.txt', fullPath: 'disk:/PayPlanner/copy-readme.txt' });
    mocks.moveYandexDiskResourceMock.mockResolvedValue({ success: true, path: '/moved.txt', fullPath: 'disk:/PayPlanner/moved.txt' });
    mocks.deleteYandexDiskResourceMock.mockResolvedValue({ success: true, path: '/readme.txt', fullPath: 'disk:/PayPlanner/readme.txt' });
    mocks.uploadYandexDiskFileMock.mockResolvedValue({ success: true, path: '/file.txt', fullPath: 'disk:/PayPlanner/file.txt' });
    mocks.downloadYandexDiskFileMock.mockResolvedValue({ blob: new Blob(['hello'], { type: 'text/plain' }), fileName: 'readme.txt' });
    mocks.getYandexDiskTextFileMock.mockResolvedValue({ path: '/readme.txt', content: 'hello' });
    mocks.saveYandexDiskTextFileMock.mockResolvedValue({ success: true, path: '/readme.txt', fullPath: 'disk:/PayPlanner/readme.txt' });
    mocks.getYandexDiskCrmSyncSettingsMock.mockResolvedValue(buildSyncSettings());
    mocks.getYandexDiskCrmSyncPreviewMock.mockResolvedValue({
      syncEnabled: false,
      overwriteExisting: false,
      conflictResolutionMode: 'yandexDiskMaster',
      rootPath: 'disk:/PayPlanner',
      items: [],
    });
    mocks.getYandexDiskCrmSyncStatusMock.mockResolvedValue({
      enabled: false,
      totalItems: 0,
      successCount: 0,
      failedCount: 0,
      pendingCount: 0,
      recentItems: [],
    });
    mocks.updateYandexDiskCrmSyncSettingsMock.mockResolvedValue(buildSyncSettings());
  });

  it('renders several connection cards and main tabs', async () => {
    render(<YandexDisk />);

    expect(await screen.findByText('Я.Диск основной')).toBeTruthy();
    expect(screen.getByText('Я.Диск архив')).toBeTruthy();
    expect(screen.getByRole('button', { name: /Подключения/i })).toBeTruthy();
    expect(screen.getAllByRole('button', { name: /Файлы/i }).length).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: /Синхронизация/i })).toBeTruthy();
    expect(screen.getByRole('button', { name: /Логи/i })).toBeTruthy();

    const card = screen.getByText('Я.Диск основной').closest('article')!;
    const filesButton = within(card).getByRole('button', { name: /Открыть файлы/i });
    const editButton = within(card).getByRole('button', { name: /Изменить/i });
    expect(filesButton.textContent?.trim()).toBe('');
    expect(editButton.textContent?.trim()).toBe('');
    expect(await screen.findAllByText(/01\.06\.2026, 08:00/)).toHaveLength(2);
    expect(filesButton.getAttribute('title')).toBe('Открыть файлы');
  });

  it('shows a recoverable reconnect state when the protected token is unavailable', async () => {
    mocks.getYandexDiskConnectionsMock.mockResolvedValue({
      connections: [buildConnection({
        isConfigured: false,
        requiresReconnect: true,
        tokenPreview: null,
      })],
    });

    render(<YandexDisk />);

    const card = (await screen.findByText('Я.Диск основной')).closest('article')!;
    expect(within(card).getByText('Нужно переподключить')).toBeTruthy();
    expect(within(card).getByText('Токен недоступен — подключите Я.Диск заново')).toBeTruthy();
    expect((within(card).getByRole('button', { name: /Открыть файлы/i }) as HTMLButtonElement).disabled).toBe(true);
    expect((within(card).getByRole('button', { name: /Проверить/i }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('opens connection editing on a separate page and protects OAuth field from autofill', async () => {
    render(<YandexDisk />);

    const card = (await screen.findByText('Я.Диск основной')).closest('article')!;
    fireEvent.click(within(card).getByRole('button', { name: /Изменить/i }));

    expect(await screen.findByText('Редактирование подключения')).toBeTruthy();
    expect(screen.getByDisplayValue('Я.Диск основной')).toBeTruthy();
    expect(screen.getByDisplayValue('disk:/PayPlanner')).toBeTruthy();

    const tokenInput = screen.getByLabelText('OAuth-токен') as HTMLInputElement;
    expect(tokenInput.closest('form')?.getAttribute('autocomplete')).toBe('off');
    expect(tokenInput.getAttribute('autocomplete')).toBe('new-password');
    expect(tokenInput.getAttribute('data-lpignore')).toBe('true');
    expect(tokenInput.getAttribute('data-1p-ignore')).toBe('true');
  });

  it('saves a new connection and keeps root path optional through default value', async () => {
    render(<YandexDisk />);

    fireEvent.click(await screen.findByRole('button', { name: /^Новое$/i }));
    fireEvent.change(screen.getByLabelText('Название'), { target: { value: 'Я.Диск новый' } });
    fireEvent.change(screen.getByLabelText('Рабочая папка'), { target: { value: '' } });
    fireEvent.change(screen.getByLabelText('OAuth-токен'), { target: { value: 'ya-new-token' } });
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/i }));

    await waitFor(() => expect(mocks.upsertYandexDiskConnectionMock).toHaveBeenCalled());
    expect(mocks.upsertYandexDiskConnectionMock).toHaveBeenCalledWith(expect.objectContaining({
      id: null,
      name: 'Я.Диск новый',
      rootPath: 'disk:/PayPlanner',
      oAuthToken: 'ya-new-token',
      isEnabled: true,
    }));
  });

  it('starts simple Yandex ID authorization for a new shared disk connection', async () => {
    const popup = { closed: false, close: vi.fn() } as unknown as Window;
    const openSpy = vi.spyOn(window, 'open').mockReturnValue(popup);
    const { unmount } = render(<YandexDisk />);

    fireEvent.click(await screen.findByRole('button', { name: /^Новое$/i }));
    fireEvent.change(screen.getByLabelText('Название'), { target: { value: 'Рабочий диск' } });
    fireEvent.click(screen.getByRole('button', { name: /^Подключить Яндекс\.Диск$/i }));

    await waitFor(() => expect(mocks.startYandexDiskAuthMock).toHaveBeenCalledWith({
      connectionId: null,
      name: 'Рабочий диск',
      rootPath: 'disk:/PayPlanner',
      isEnabled: true,
      returnUrl: '/?ppTab=settings&settingsSection=yandex-disk',
    }));
    expect(openSpy).toHaveBeenCalledWith(
      'https://oauth.yandex.ru/authorize?state=oauth-state',
      'pp-yandex-disk-link',
      'popup=yes,width=560,height=720',
    );

    unmount();
    expect(popup.close).toHaveBeenCalled();
    openSpy.mockRestore();
  });

  it('loads files by selected connection and opens a normal copy dialog', async () => {
    render(<YandexDisk />);

    const card = (await screen.findByText('Я.Диск основной')).closest('article')!;
    fireEvent.click(within(card).getByRole('button', { name: /Открыть файлы/i }));

    expect(await screen.findByText('readme.txt')).toBeTruthy();
    await waitFor(() => expect(mocks.listYandexDiskResourcesMock).toHaveBeenCalledWith('/', 'ya-main'));

    const row = screen.getByText('readme.txt').closest('tr')!;
    fireEvent.click(within(row).getByRole('button', { name: /Копировать/i }));

    expect(await screen.findByText('Копировать объект Я.Диска')).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Новый путь копии'), { target: { value: '/copy-readme.txt' } });
    const copyForm = screen.getByText('Копировать объект Я.Диска').closest('form')!;
    fireEvent.click(within(copyForm).getByRole('button', { name: /^Копировать$/i }));

    await waitFor(() => expect(mocks.copyYandexDiskResourceMock).toHaveBeenCalledWith('/readme.txt', '/copy-readme.txt', false, 'ya-main'));
  });
});
