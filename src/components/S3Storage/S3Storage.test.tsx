import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { S3Storage } from './S3Storage';

const mocks = vi.hoisted(() => ({
  toastSuccessMock: vi.fn(),
  getS3StorageConnectionsMock: vi.fn(),
  upsertS3StorageConnectionMock: vi.fn(),
  deleteS3StorageConnectionMock: vi.fn(),
  testS3StorageConnectionMock: vi.fn(),
  listS3StorageObjectsMock: vi.fn(),
  createS3StorageFolderMock: vi.fn(),
  uploadS3StorageFileMock: vi.fn(),
  downloadS3StorageFileMock: vi.fn(),
  deleteS3StorageObjectMock: vi.fn(),
  copyS3StorageObjectMock: vi.fn(),
  moveS3StorageObjectMock: vi.fn(),
  getS3StorageTextFileMock: vi.fn(),
  saveS3StorageTextFileMock: vi.fn(),
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
    listS3StorageObjects: mocks.listS3StorageObjectsMock,
    createS3StorageFolder: mocks.createS3StorageFolderMock,
    uploadS3StorageFile: mocks.uploadS3StorageFileMock,
    downloadS3StorageFile: mocks.downloadS3StorageFileMock,
    deleteS3StorageObject: mocks.deleteS3StorageObjectMock,
    copyS3StorageObject: mocks.copyS3StorageObjectMock,
    moveS3StorageObject: mocks.moveS3StorageObjectMock,
    getS3StorageTextFile: mocks.getS3StorageTextFileMock,
    saveS3StorageTextFile: mocks.saveS3StorageTextFileMock,
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
    mocks.listS3StorageObjectsMock.mockImplementation((_id: string, path: string) => Promise.resolve({
      current: { name: path === '/' ? 'S3' : 'docs', path, key: path === '/' ? 'payplanner' : 'payplanner/docs', type: 'folder' },
      items: path === '/'
        ? [
            { name: 'docs', path: '/docs', key: 'payplanner/docs', type: 'folder' },
            { name: 'readme.txt', path: '/readme.txt', key: 'payplanner/readme.txt', type: 'file', size: 12, lastModified: '2026-06-01T12:00:00+07:00' },
          ]
        : [
            { name: 'note.txt', path: '/docs/note.txt', key: 'payplanner/docs/note.txt', type: 'file', size: 5, lastModified: '2026-06-01T12:10:00+07:00' },
          ],
      limit: 100,
      offset: 0,
      total: path === '/' ? 2 : 1,
    }));
    mocks.createS3StorageFolderMock.mockResolvedValue({ success: true, path: '/new-folder', key: 'payplanner/new-folder' });
    mocks.uploadS3StorageFileMock.mockResolvedValue({ success: true, path: '/file.txt', key: 'payplanner/file.txt' });
    mocks.downloadS3StorageFileMock.mockResolvedValue({ blob: new Blob(['hello'], { type: 'text/plain' }), fileName: 'readme.txt' });
    mocks.deleteS3StorageObjectMock.mockResolvedValue({ success: true, path: '/readme.txt', key: 'payplanner/readme.txt' });
    mocks.copyS3StorageObjectMock.mockResolvedValue({ success: true, path: '/copy.txt', key: 'payplanner/copy.txt' });
    mocks.moveS3StorageObjectMock.mockResolvedValue({ success: true, path: '/moved.txt', key: 'payplanner/moved.txt' });
    mocks.getS3StorageTextFileMock.mockResolvedValue({ path: '/readme.txt', content: 'hello' });
    mocks.saveS3StorageTextFileMock.mockResolvedValue({ success: true, path: '/readme.txt', key: 'payplanner/readme.txt' });
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

  it('loads object list and opens S3 folders', async () => {
    render(<S3Storage />);

    expect(await screen.findByText('Файлы S3')).toBeTruthy();
    expect(await screen.findByText('readme.txt')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /^docs$/i }));

    await waitFor(() => expect(mocks.listS3StorageObjectsMock).toHaveBeenCalledWith('hostkey-main', '/docs'));
    expect(await screen.findByText('note.txt')).toBeTruthy();
  });

  it('opens and saves a text file in S3', async () => {
    render(<S3Storage />);

    await screen.findByText('readme.txt');
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать readme.txt' }));

    const editor = await screen.findByDisplayValue('hello');
    fireEvent.change(editor, { target: { value: 'updated' } });
    const saveButtons = screen.getAllByRole('button', { name: /^Сохранить$/i });
    fireEvent.click(saveButtons[saveButtons.length - 1]);

    await waitFor(() => expect(mocks.saveS3StorageTextFileMock).toHaveBeenCalledWith('hostkey-main', '/readme.txt', 'updated', true));
  });
});
