import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { S3Storage } from './S3Storage';

const mocks = vi.hoisted(() => ({
  toastSuccessMock: vi.fn(),
  toastErrorMock: vi.fn(),
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
    error: mocks.toastErrorMock,
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

function buildConnection(overrides = {}) {
  return {
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
    ...overrides,
  };
}

function buildOverview(connections = [buildConnection()]) {
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
    connections,
  };
}

describe('S3Storage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.getS3StorageConnectionsMock.mockResolvedValue(buildOverview());
    mocks.upsertS3StorageConnectionMock.mockResolvedValue(buildConnection({ name: 'HOSTKEY backup', rootPrefix: '' }));
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

  it('renders S3 connection cards and main tabs', async () => {
    render(<S3Storage />);

    expect(await screen.findByText('HOSTKEY основной')).toBeTruthy();
    expect(screen.getByRole('button', { name: /Подключения/i })).toBeTruthy();
    expect(screen.getAllByRole('button', { name: /Файлы/i }).length).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: /Логи/i })).toBeTruthy();
    expect(screen.getByText('AKIA...1234')).toBeTruthy();
    expect(screen.queryByDisplayValue('https://s3-nl.hostkey.com')).toBeNull();
  });

  it('opens connection editing on a separate page', async () => {
    render(<S3Storage />);

    const card = (await screen.findByText('HOSTKEY основной')).closest('article')!;
    fireEvent.click(within(card).getByRole('button', { name: /Изменить/i }));

    expect(await screen.findByText('Редактирование подключения')).toBeTruthy();
    expect(screen.getByDisplayValue('https://s3-nl.hostkey.com')).toBeTruthy();
    expect(screen.getByLabelText(/Рабочая папка/i)).toBeTruthy();

    const accessKeyInput = screen.getByLabelText('Access Key') as HTMLInputElement;
    const secretKeyInput = screen.getByLabelText('Secret Key') as HTMLInputElement;
    expect(accessKeyInput.closest('form')?.getAttribute('autocomplete')).toBe('off');
    expect(accessKeyInput.getAttribute('autocomplete')).toBe('new-password');
    expect(accessKeyInput.getAttribute('data-lpignore')).toBe('true');
    expect(secretKeyInput.getAttribute('autocomplete')).toBe('new-password');
    expect(secretKeyInput.getAttribute('data-1p-ignore')).toBe('true');
  });

  it('saves edited connection without exposing existing secret', async () => {
    render(<S3Storage />);

    const card = (await screen.findByText('HOSTKEY основной')).closest('article')!;
    fireEvent.click(within(card).getByRole('button', { name: /Изменить/i }));
    fireEvent.change(await screen.findByLabelText('Название'), { target: { value: 'HOSTKEY backup' } });
    fireEvent.change(screen.getByLabelText(/Рабочая папка/i), { target: { value: '' } });
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/i }));

    await waitFor(() => expect(mocks.upsertS3StorageConnectionMock).toHaveBeenCalled());
    expect(mocks.upsertS3StorageConnectionMock).toHaveBeenCalledWith(expect.objectContaining({
      id: 'hostkey-main',
      name: 'HOSTKEY backup',
      rootPrefix: '',
      secretAccessKey: '',
      clearSecret: false,
    }));
    expect(mocks.toastSuccessMock).toHaveBeenCalledWith('Подключение S3 обновлено.', 'S3');
  });

  it('creates connection with optional empty root prefix', async () => {
    mocks.getS3StorageConnectionsMock.mockResolvedValue(buildOverview([]));
    mocks.upsertS3StorageConnectionMock.mockResolvedValue(buildConnection({ id: 'new-s3', name: 'HOSTKEY S3', rootPrefix: '' }));

    render(<S3Storage />);

    fireEvent.click(await screen.findByRole('button', { name: /Новое подключение/i }));
    fireEvent.change(screen.getByLabelText('Bucket'), { target: { value: 'new-bucket' } });
    fireEvent.change(screen.getByLabelText('Access Key'), { target: { value: 'access-key' } });
    fireEvent.change(screen.getByLabelText('Secret Key'), { target: { value: 'secret-key' } });
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/i }));

    await waitFor(() => expect(mocks.upsertS3StorageConnectionMock).toHaveBeenCalledWith(expect.objectContaining({
      bucket: 'new-bucket',
      rootPrefix: '',
      accessKeyId: 'access-key',
      secretAccessKey: 'secret-key',
    })));
  });

  it('runs connection test and shows it in logs', async () => {
    render(<S3Storage />);

    const card = (await screen.findByText('HOSTKEY основной')).closest('article')!;
    fireEvent.click(within(card).getByRole('button', { name: /Проверить/i }));

    await waitFor(() => expect(mocks.testS3StorageConnectionMock).toHaveBeenCalledWith('hostkey-main'));
    expect(await screen.findByText('Последняя проверка')).toBeTruthy();
    expect(screen.getByText('Список объектов')).toBeTruthy();
  });

  it('loads object list and opens S3 folders from files tab', async () => {
    render(<S3Storage />);

    const card = (await screen.findByText('HOSTKEY основной')).closest('article')!;
    fireEvent.click(within(card).getByRole('button', { name: /Файлы/i }));

    expect(await screen.findByText('readme.txt')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /^docs$/i }));

    await waitFor(() => expect(mocks.listS3StorageObjectsMock).toHaveBeenCalledWith('hostkey-main', '/docs'));
    expect(await screen.findByText('note.txt')).toBeTruthy();
  });

  it('opens and saves a text file in S3', async () => {
    render(<S3Storage />);

    const card = (await screen.findByText('HOSTKEY основной')).closest('article')!;
    fireEvent.click(within(card).getByRole('button', { name: /Файлы/i }));
    await screen.findByText('readme.txt');
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать readme.txt' }));

    const editor = await screen.findByDisplayValue('hello');
    fireEvent.change(editor, { target: { value: 'updated' } });
    const saveButtons = screen.getAllByRole('button', { name: /^Сохранить$/i });
    fireEvent.click(saveButtons[saveButtons.length - 1]);

    await waitFor(() => expect(mocks.saveS3StorageTextFileMock).toHaveBeenCalledWith('hostkey-main', '/readme.txt', 'updated', true));
  });
});
