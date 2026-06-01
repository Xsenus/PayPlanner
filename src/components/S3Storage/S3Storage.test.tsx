import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { S3Storage } from './S3Storage';

const mocks = vi.hoisted(() => ({
  toastSuccessMock: vi.fn(),
  getS3StorageConnectionsMock: vi.fn(),
  upsertS3StorageConnectionMock: vi.fn(),
  deleteS3StorageConnectionMock: vi.fn(),
  testS3StorageConnectionMock: vi.fn(),
}));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: { id: 1, role: { id: 1, name: 'admin' } },
  }),
}));

vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: () => ({
    s3Storage: {
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
    error: vi.fn(),
  }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getS3StorageConnections: mocks.getS3StorageConnectionsMock,
    upsertS3StorageConnection: mocks.upsertS3StorageConnectionMock,
    deleteS3StorageConnection: mocks.deleteS3StorageConnectionMock,
    testS3StorageConnection: mocks.testS3StorageConnectionMock,
  },
}));

function buildOverview() {
  return {
    providerPresets: [
      {
        key: 'hostkey',
        name: 'HOSTKEY S3',
        endpoint: 'https://s3-nl.hostkey.com',
        region: 'nl',
        forcePathStyle: true,
      },
    ],
    connections: [
      {
        id: 'hostkey-main',
        name: 'HOSTKEY основной',
        provider: 'hostkey',
        providerName: 'HOSTKEY S3',
        endpoint: 'https://s3-nl.hostkey.com',
        region: 'nl',
        bucket: 'payplanner',
        rootPrefix: 'payplanner',
        isEnabled: true,
        isConfigured: true,
        accessKeyPreview: 'AKIA...1234',
        secretKeyPreview: '****5678',
        createdAt: '2026-06-01T10:00:00+07:00',
        updatedAt: '2026-06-01T11:00:00+07:00',
      },
    ],
  };
}

describe('S3Storage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.getS3StorageConnectionsMock.mockResolvedValue(buildOverview());
    mocks.upsertS3StorageConnectionMock.mockResolvedValue(buildOverview().connections[0]);
    mocks.deleteS3StorageConnectionMock.mockResolvedValue(undefined);
    mocks.testS3StorageConnectionMock.mockResolvedValue({
      success: true,
      connectionId: 'hostkey-main',
      endpoint: 'https://s3-nl.hostkey.com',
      region: 'nl',
      bucket: 'payplanner',
      testKey: 'payplanner/_payplanner-test/test.txt',
      checkedAt: '2026-06-01T12:00:00+07:00',
      steps: [
        { key: 'list', name: 'Список объектов', success: true, message: 'Bucket доступен.' },
        { key: 'upload', name: 'Загрузка файла', success: true, message: 'Файл загружен.' },
      ],
    });
  });

  it('renders S3 connections and HOSTKEY defaults', async () => {
    render(<S3Storage />);

    expect(await screen.findByText('HOSTKEY основной')).toBeTruthy();
    expect(screen.getByDisplayValue('https://s3-nl.hostkey.com')).toBeTruthy();
    expect(screen.getByDisplayValue('nl')).toBeTruthy();
    expect(screen.getByText('AKIA...1234')).toBeTruthy();
  });

  it('runs connection test from the list', async () => {
    render(<S3Storage />);

    await screen.findByText('HOSTKEY основной');
    fireEvent.click(screen.getByRole('button', { name: /Проверить/i }));

    await waitFor(() => expect(mocks.testS3StorageConnectionMock).toHaveBeenCalledWith('hostkey-main'));
    expect(await screen.findByText('Результат проверки')).toBeTruthy();
    expect(screen.getByText('Список объектов')).toBeTruthy();
  });

  it('saves edited connection without exposing existing secret', async () => {
    render(<S3Storage />);

    await screen.findByText('HOSTKEY основной');
    fireEvent.click(screen.getByRole('button', { name: /Изменить/i }));
    fireEvent.change(screen.getByLabelText('Название'), { target: { value: 'HOSTKEY backup' } });
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/i }));

    await waitFor(() => expect(mocks.upsertS3StorageConnectionMock).toHaveBeenCalled());
    expect(mocks.upsertS3StorageConnectionMock).toHaveBeenCalledWith(expect.objectContaining({
      id: 'hostkey-main',
      name: 'HOSTKEY backup',
      secretAccessKey: '',
      clearSecret: false,
    }));
  });
});
