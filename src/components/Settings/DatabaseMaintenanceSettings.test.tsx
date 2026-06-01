import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { DatabaseMaintenanceSettings } from './DatabaseMaintenanceSettings';

const mocks = vi.hoisted(() => ({
  toastMock: {
    error: vi.fn(),
    success: vi.fn(),
  },
  updateDatabaseBackupSettingsMock: vi.fn(),
  getDatabaseStatusMock: vi.fn(),
  getUserActivityMaintenanceMock: vi.fn(),
  getDatabaseBackupOverviewMock: vi.fn(),
  getS3StorageConnectionsMock: vi.fn(),
  retryDatabaseBackupS3UploadMock: vi.fn(),
}));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: { id: 1, role: { id: 1, name: 'admin' } },
    isAdmin: () => true,
  }),
}));

vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: () => ({
    audit: {
      canView: true,
      canEdit: true,
    },
  }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({
    confirm: vi.fn(),
  }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => mocks.toastMock,
}));

vi.mock('./ObjectAuditDeletedObjectsSettings', () => ({
  ObjectAuditDeletedObjectsSettings: () => <div>Deleted objects</div>,
}));

vi.mock('./SystemJobDiagnosticsSettings', () => ({
  SystemJobDiagnosticsSettings: () => <div>System jobs</div>,
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getDatabaseStatus: mocks.getDatabaseStatusMock,
    getUserActivityMaintenance: mocks.getUserActivityMaintenanceMock,
    getDatabaseBackupOverview: mocks.getDatabaseBackupOverviewMock,
    getS3StorageConnections: mocks.getS3StorageConnectionsMock,
    updateDatabaseBackupSettings: mocks.updateDatabaseBackupSettingsMock,
    retryDatabaseBackupS3Upload: mocks.retryDatabaseBackupS3UploadMock,
  },
}));

function buildBackupOverview() {
  return {
    provider: 'sqlite',
    providerDisplayName: 'SQLite',
    backupFormat: 'ZIP-архив',
    fileExtension: '.zip',
    selectedFormat: 'sqlite-zip',
    availableFormats: [
      {
        key: 'sqlite-zip',
        label: 'ZIP-архив',
        description: 'Архив базы данных',
        fileExtension: '.zip',
        isDefault: true,
      },
    ],
    automaticEnabled: true,
    scheduleLocal: '00:00,06:00,12:00,18:00',
    retentionDays: 8,
    rootDirectory: 'backups/database',
    timeZone: 'Asia/Novosibirsk',
    s3Upload: {
      enabled: false,
      connectionIds: [],
      prefixTemplate: 'backups/database/{yyyy}/{MM}/',
    },
    s3UploadResults: [
      {
        id: 'result-1',
        backupRelativePath: '2026/06/backup.zip',
        backupFileName: 'backup.zip',
        connectionId: 'hostkey-main',
        status: 'success',
        targetPath: '/backups/database/2026/06/backup.zip',
        targetKey: 'payplanner/backups/database/2026/06/backup.zip',
        sizeBytes: 1024,
        createdAt: '2026-06-01T10:00:00+07:00',
        updatedAt: '2026-06-01T10:01:00+07:00',
      },
    ],
    items: [],
  };
}

describe('DatabaseMaintenanceSettings', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.getDatabaseStatusMock.mockResolvedValue({
      provider: 'sqlite',
      displayName: 'SQLite',
      supportsSqliteMaintenance: true,
    });
    mocks.getUserActivityMaintenanceMock.mockResolvedValue({
      isEnabled: true,
      retentionDays: 30,
      storage: { totalSizeBytes: 0 },
    });
    mocks.getDatabaseBackupOverviewMock.mockResolvedValue(buildBackupOverview());
    mocks.getS3StorageConnectionsMock.mockResolvedValue({
      providerPresets: [],
      connections: [
        {
          id: 'hostkey-main',
          name: 'HOSTKEY backup',
          provider: 'hostkey',
          providerName: 'HOSTKEY S3',
          endpoint: 'https://s3-nl.hostkey.com',
          region: 'nl',
          bucket: 'payplanner',
          rootPrefix: 'prod',
          isEnabled: true,
          isConfigured: true,
          createdAt: '2026-06-01T10:00:00+07:00',
          updatedAt: '2026-06-01T10:00:00+07:00',
        },
      ],
    });
    mocks.updateDatabaseBackupSettingsMock.mockResolvedValue({
      ...buildBackupOverview(),
      s3Upload: {
        enabled: true,
        connectionIds: ['hostkey-main'],
        prefixTemplate: 'backup-db/{yyyy}/',
      },
    });
    mocks.retryDatabaseBackupS3UploadMock.mockResolvedValue(buildBackupOverview());
  });

  it('saves S3 upload settings for database backups', async () => {
    render(<DatabaseMaintenanceSettings />);

    await waitFor(() => expect(mocks.getDatabaseBackupOverviewMock).toHaveBeenCalled());
    fireEvent.click(screen.getAllByRole('button')[4]);

    expect(await screen.findByText('Отправка backup в S3')).toBeTruthy();

    fireEvent.click(screen.getByLabelText(/Загружать новые резервные копии в S3/i));
    fireEvent.click(screen.getByLabelText(/HOSTKEY backup/i));
    fireEvent.change(screen.getByDisplayValue('backups/database/{yyyy}/{MM}/'), {
      target: { value: 'backup-db/{yyyy}/' },
    });
    fireEvent.click(screen.getAllByRole('button').find((button) => button.textContent?.includes('Сохранить'))!);

    await waitFor(() => expect(mocks.updateDatabaseBackupSettingsMock).toHaveBeenCalledWith(expect.objectContaining({
      s3UploadEnabled: true,
      s3ConnectionIds: ['hostkey-main'],
      s3PrefixTemplate: 'backup-db/{yyyy}/',
    })));
  });

  it('retries failed S3 upload for database backup', async () => {
    const failedOverview = {
      ...buildBackupOverview(),
      s3Upload: {
        enabled: true,
        connectionIds: ['hostkey-main'],
        prefixTemplate: 'backups/database/{yyyy}/{MM}/',
      },
      s3UploadResults: [
        {
          id: 'failed-result',
          backupRelativePath: '2026/06/backup.zip',
          backupFileName: 'backup.zip',
          connectionId: 'hostkey-main',
          status: 'failed',
          targetPath: '/backups/database/2026/06/backup.zip',
          targetKey: null,
          sizeBytes: 1024,
          error: 'S3 unavailable',
          createdAt: '2026-06-01T10:00:00+07:00',
          updatedAt: '2026-06-01T10:01:00+07:00',
        },
      ],
    };
    mocks.getDatabaseBackupOverviewMock.mockResolvedValue(failedOverview);
    mocks.retryDatabaseBackupS3UploadMock.mockResolvedValue({
      ...failedOverview,
      s3UploadResults: [
        {
          ...failedOverview.s3UploadResults[0],
          status: 'success',
          error: null,
        },
      ],
    });

    render(<DatabaseMaintenanceSettings />);

    await waitFor(() => expect(mocks.getDatabaseBackupOverviewMock).toHaveBeenCalled());
    fireEvent.click(screen.getAllByRole('button')[4]);
    fireEvent.click(await screen.findByRole('button', { name: /Повторить/i }));

    await waitFor(() => expect(mocks.retryDatabaseBackupS3UploadMock).toHaveBeenCalledWith('failed-result'));
    expect(await screen.findByText('S3-выгрузка резервной копии повторена.')).toBeTruthy();
  });
});
