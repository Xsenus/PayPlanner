import { StrictMode } from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { documentCopyTestSession } from '../../test/documentCopySession';
import type { User } from '../../services/authService';
import { buildProfileUpdatePayload, ProfilePage } from './ProfilePage';

const authMock = vi.hoisted(() => ({
  getProfile: vi.fn(),
  getAuthProviders: vi.fn(),
  getContractorBalance: vi.fn(),
  attachExternalIdentity: vi.fn(),
  detachExternalIdentity: vi.fn(),
  updateProfile: vi.fn(),
  changePassword: vi.fn(),
  uploadProfilePhoto: vi.fn(),
  startExternalAuth: vi.fn(),
  startTelegramBotAuth: vi.fn(),
  getExternalAuthStatus: vi.fn(),
}));

const apiMock = vi.hoisted(() => ({
  getUserSetting: vi.fn(),
  upsertUserSetting: vi.fn(),
  initializeUserSetting: vi.fn(),
}));

const permissionsState = vi.hoisted(() => ({
  permissions: {
    calendar: { canView: true, viewScope: 'own' },
    audit: { canView: true },
    payments: { canView: true },
  },
}));

const authState = vi.hoisted(() => ({ user: undefined as User | undefined }));
let testSession: ReturnType<typeof documentCopyTestSession>;
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: Error) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
afterEach(() => { testSession?.restore(); vi.restoreAllMocks(); vi.unstubAllGlobals(); window.history.replaceState({}, '', '/'); });

type CropProps = { open: boolean; onConfirm: (file: File) => Promise<void> | void; onClose: () => void };
const cropMock = vi.hoisted(() => ({ latest: null as CropProps | null }));
const confirmMock = vi.hoisted(() => vi.fn());

const refreshUserMock = vi.hoisted(() => vi.fn());
const toastMock = vi.hoisted(() => ({
  error: vi.fn(),
  success: vi.fn(),
}));

vi.mock('../../services/authService', async importOriginal => ({
  ...await importOriginal<typeof import('../../services/authService')>(),
  authService: authMock,
}));

vi.mock('../../services/api', () => ({
  apiService: apiMock,
}));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    refreshUser: refreshUserMock,
    user: authState.user,
  }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({
    confirm: confirmMock,
  }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => toastMock,
}));

vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: () => permissionsState.permissions,
}));

vi.mock('./GoogleCalendarPanel', () => ({
  GoogleCalendarPanel: () => <div>Google Calendar mock</div>,
}));

vi.mock('./YandexCalendarPanel', () => ({
  YandexCalendarPanel: () => <div>Яндекс.Календарь mock</div>,
}));

vi.mock('./ProfilePhotoCropModal', () => ({
  ProfilePhotoCropModal: (props: CropProps) => { cropMock.latest = props; return props.open ? <><button onClick={() => void Promise.resolve(props.onConfirm(new File(['image'], 'avatar.png', { type: 'image/png' }))).catch(() => undefined)}>Подтвердить фото</button><button onClick={props.onClose}>Закрыть кадрирование</button></> : null; },
}));

vi.mock('./MaxMessengerPanel', () => ({
  MaxMessengerPanel: () => <div>Уведомления в MAX mock</div>,
}));

vi.mock('../Notifications/WebPushDevicePanel', () => ({ WebPushDevicePanel: ({ title }: { title: string }) => <div>{title}</div> }));

vi.mock('../Tasks/TaskNotificationDefaultsPanel', () => ({
  TaskNotificationDefaultsPanel: ({ onChange }: { onChange: (value: { reminders: string[]; channels: string[] }) => void }) => <div>Настройки напоминаний mock<button onClick={() => onChange({ reminders: ['atDue'], channels: ['inApp'] })}>Выбрать напоминания</button></div>,
}));

vi.mock('../ui/ObjectAuditModal', () => ({
  ObjectAuditLauncher: ({ triggerTitle }: { triggerTitle: string }) => <div>{triggerTitle}</div>,
}));

function createProfile(options?: { roleName?: string; isExternalContractor?: boolean }) {
  return {
    id: 7,
    email: 'user@example.com',
    fullName: 'Иван Иванов',
    firstName: 'Иван',
    lastName: 'Иванов',
    middleName: '',
    dateOfBirth: '1990-01-10T00:00:00Z',
    phoneNumber: '+79990000000',
    whatsApp: '',
    telegram: '',
    instagram: '',
    messenger: '',
    viber: '',
    photoUrl: null,
    role: { id: 7, name: options?.roleName ?? 'admin' },
    isExternalContractor: options?.isExternalContractor ?? false,
    externalIdentities: [
      {
        provider: 'google',
        displayName: 'Ivan Ivanov',
        providerEmail: 'user@gmail.com',
        providerUsername: null,
      },
    ],
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-02T00:00:00Z',
    createdByUserName: 'Администратор',
    updatedByUserName: 'Администратор',
  };
}

describe('ProfilePage', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    testSession = documentCopyTestSession();
    authState.user = createProfile() as User;
    cropMock.latest = null; confirmMock.mockResolvedValue(true);
    authMock.updateProfile.mockResolvedValue(createProfile()); authMock.changePassword.mockResolvedValue(createProfile()); authMock.uploadProfilePhoto.mockResolvedValue(createProfile());
    authMock.detachExternalIdentity.mockResolvedValue({ detached: true, user: createProfile() });
    authMock.startExternalAuth.mockResolvedValue({ state: 'synthetic-state', authUrl: 'https://example.test/auth' });
    authMock.startTelegramBotAuth.mockResolvedValue({ state: 'synthetic-state', authUrl: 'https://example.test/auth' });
    vi.spyOn(window, 'open').mockReturnValue({ closed: false, close: vi.fn() } as unknown as Window);
    apiMock.initializeUserSetting.mockResolvedValue({ key: 'profile.timeZoneId', value: 'UTC' });
    permissionsState.permissions = {
      calendar: { canView: true, viewScope: 'own' },
      audit: { canView: true },
      payments: { canView: true },
    };
    authMock.getProfile.mockResolvedValue(createProfile());
    authMock.getAuthProviders.mockResolvedValue({
      googleEnabled: true,
      vkEnabled: true,
      yandexEnabled: true,
      telegramEnabled: false,
      telegramWidgetEnabled: false,
      telegramBotUsername: null,
    });
    authMock.getContractorBalance.mockResolvedValue({
      owedToMe: 0,
      owedByMe: 0,
      paidToMe: 0,
      paidByMe: 0,
      queue: [],
      paid: [],
    });
    apiMock.getUserSetting.mockResolvedValue(null);
    apiMock.upsertUserSetting.mockResolvedValue({ key: 'profile.timeZoneId', value: 'Asia/Novosibirsk' });
  });

  it('передаёт очистку необязательных контактов и даты рождения', () => {
    const form: Parameters<typeof buildProfileUpdatePayload>[0] = {
      email: ' user@example.com ',
      fullName: ' Иван Иванов ',
      firstName: 'Иван',
      lastName: 'Иванов',
      middleName: '',
      dateOfBirth: '',
      phoneNumber: '',
      whatsApp: '',
      telegram: '',
      instagram: '',
      messenger: '',
      viber: '',
    };

    expect(buildProfileUpdatePayload(form)).toEqual({
      email: 'user@example.com',
      fullName: 'Иван Иванов',
      firstName: 'Иван',
      lastName: 'Иванов',
      middleName: '',
      dateOfBirth: undefined,
      clearDateOfBirth: true,
      phoneNumber: '',
      whatsApp: '',
      telegram: '',
      instagram: '',
      messenger: '',
      viber: '',
    });
  });

  it('splits profile sections into tabs', async () => {
    render(<ProfilePage />);

    expect(await screen.findByText('Контактные данные')).toBeTruthy();
    expect(screen.getByText('Push-уведомления')).toBeTruthy();
    expect(screen.queryByText('Google Calendar mock')).toBeNull();
    expect(screen.queryByText('Способы входа')).toBeNull();

    fireEvent.click(screen.getByRole('tab', { name: /Календари/i }));
    expect(await screen.findByText('Google Calendar mock')).toBeTruthy();
    expect(screen.getByText('Яндекс.Календарь mock')).toBeTruthy();
    expect(screen.queryByText('Контактные данные')).toBeNull();

    fireEvent.click(screen.getByRole('tab', { name: /Вход и безопасность/i }));
    expect(await screen.findByText('Способы входа')).toBeTruthy();
    expect(screen.getByText('Смена пароля')).toBeTruthy();
    expect(screen.queryByText('Google Calendar mock')).toBeNull();

    fireEvent.click(screen.getByRole('tab', { name: /Аудит/i }));
    expect(await screen.findByText('Аудит профиля')).toBeTruthy();
    expect(screen.queryByText('Смена пароля')).toBeNull();

    await waitFor(() => expect(authMock.getProfile).toHaveBeenCalledTimes(1));
  });

  it('hides the profile audit tab for a non-admin role without audit permission', async () => {
    permissionsState.permissions = {
      calendar: { canView: true, viewScope: 'own' },
      audit: { canView: false },
      payments: { canView: true },
    };
    authState.user = createProfile({ roleName: 'lawyer' }) as User;
    authMock.getProfile.mockResolvedValueOnce(authState.user);

    render(<ProfilePage />);

    expect(await screen.findByText('Контактные данные')).toBeTruthy();
    expect(screen.queryByRole('tab', { name: /Аудит/i })).toBeNull();
    expect(screen.queryByText('Аудит профиля')).toBeNull();
    expect(screen.getByRole('tab', { name: /Профиль/i })).toBeTruthy();
  });

  it('hides calendar connection panels when the role cannot view the calendar section', async () => {
    permissionsState.permissions = {
      calendar: { canView: false, viewScope: 'own' },
      audit: { canView: true },
      payments: { canView: true },
    };
    authState.user = createProfile({ roleName: 'lawyer' }) as User;
    authMock.getProfile.mockResolvedValueOnce(authState.user);

    render(<ProfilePage />);

    expect(await screen.findByText('Контактные данные')).toBeTruthy();
    expect(screen.queryByRole('tab', { name: /Календари/i })).toBeNull();
    expect(screen.queryByText('Google Calendar mock')).toBeNull();
    expect(screen.queryByText('Яндекс.Календарь mock')).toBeNull();
  });

  it('shows all contractor task payments in the personal profile', async () => {
    authMock.getProfile.mockResolvedValueOnce(createProfile({ roleName: 'lawyer', isExternalContractor: true }));
    authMock.getContractorBalance.mockResolvedValueOnce({
      owedToMe: 7000,
      owedByMe: 0,
      paidToMe: 4500,
      paidByMe: 0,
      queue: [
        {
          taskId: 101,
          taskTitle: 'Waiting contractor task',
          paymentId: 1001,
          direction: 'toMe',
          assigneeName: 'Sergey',
          authorName: 'Anna',
          clientId: 1,
          clientName: 'Client One',
          legalEntityId: null,
          legalEntityName: null,
          clientCaseId: 11,
          clientCaseTitle: 'Case One',
          amount: 2500,
          paidAmount: 0,
          outstandingAmount: 2500,
          paymentStatus: 'Pending',
          taskStatus: 'Done',
          paymentDueDate: '2026-06-30T00:00:00Z',
          paymentDate: '2026-06-29T00:00:00Z',
          paidDate: null,
          paymentSourceName: 'Юридические услуги',
          paymentAccount: 'Б/Н',
          paymentAccountDate: '2026-06-29T00:00:00Z',
          paymentOrderNumber: '1001-А',
          paymentDocumentDate: '2026-06-29T00:00:00Z',
        },
        {
          taskId: 102,
          taskTitle: 'Result contractor task',
          paymentId: null,
          direction: 'toMe',
          assigneeName: 'Sergey',
          authorName: 'Anna',
          clientId: 2,
          clientName: 'Client Two',
          legalEntityId: null,
          legalEntityName: null,
          clientCaseId: 22,
          clientCaseTitle: 'Case Two',
          amount: 3500,
          paidAmount: 0,
          outstandingAmount: 3500,
          paymentStatus: 'WaitingForResult',
          taskStatus: 'InProgress',
          paymentDueDate: null,
          paidDate: null,
        },
        {
          taskId: 103,
          taskTitle: 'Sixth visible task',
          paymentId: 1003,
          direction: 'toMe',
          assigneeName: 'Sergey',
          authorName: 'Anna',
          clientId: 3,
          clientName: 'Client Three',
          legalEntityId: null,
          legalEntityName: null,
          clientCaseId: 33,
          clientCaseTitle: 'Case Three',
          amount: 1000,
          paidAmount: 0,
          outstandingAmount: 1000,
          paymentStatus: 'Overdue',
          taskStatus: 'Done',
          paymentDueDate: '2026-06-01T00:00:00Z',
          paidDate: null,
        },
      ],
      paid: [
        {
          taskId: 104,
          taskTitle: 'Paid contractor task',
          paymentId: 1004,
          direction: 'toMe',
          assigneeName: 'Sergey',
          authorName: 'Anna',
          clientId: 4,
          clientName: 'Client Four',
          legalEntityId: null,
          legalEntityName: null,
          clientCaseId: 44,
          clientCaseTitle: 'Case Four',
          amount: 4500,
          paidAmount: 4500,
          outstandingAmount: 0,
          paymentStatus: 'Completed',
          taskStatus: 'Done',
          paymentDueDate: '2026-06-10T00:00:00Z',
          paidDate: '2026-06-11T00:00:00Z',
        },
      ],
    });

    render(<ProfilePage />);

    await waitFor(() => expect(screen.getAllByRole('tab').length).toBeGreaterThan(1));
    fireEvent.click(screen.getAllByRole('tab')[1]);

    const taskLink = await screen.findByRole('link', { name: 'Waiting contractor task' });
    const clientLink = screen.getByRole('link', { name: 'Client One' });
    const caseLink = screen.getByRole('link', { name: 'Case One' });
    const paymentLink = screen.getByRole('link', { name: /#1001/ });

    expect(taskLink.getAttribute('href')).toBe('/tasks/101');
    expect(taskLink.getAttribute('target')).toBe('_blank');
    expect(clientLink.getAttribute('href')).toBe('/clients/1');
    expect(clientLink.getAttribute('target')).toBe('_blank');
    expect(caseLink.getAttribute('href')).toBe('/cases/11');
    expect(caseLink.getAttribute('target')).toBe('_blank');
    expect(paymentLink.getAttribute('href')).toBe('/payments/1001?ppTab=paymentsExpense');
    expect(paymentLink.getAttribute('target')).toBe('_blank');
    expect(screen.getByText('дата платежа 29.06.2026')).toBeTruthy();
    expect(screen.getByText('источник Юридические услуги')).toBeTruthy();
    expect(screen.getByText('счет Б/Н от 29.06.2026')).toBeTruthy();
    expect(screen.getByText('документ 1001-А от 29.06.2026')).toBeTruthy();
    expect(screen.getByText('Result contractor task')).toBeTruthy();
    expect(screen.getByText('Sixth visible task')).toBeTruthy();
    expect(screen.getByText('Paid contractor task')).toBeTruthy();
    expect(screen.getByText(/#1001/)).toBeTruthy();
    expect(screen.getByText(/#1004/)).toBeTruthy();
  });
  it('ownership baseline: ignores the former login profile response', async () => {
    const pending = deferred<ReturnType<typeof createProfile>>(); authMock.getProfile.mockReturnValueOnce(pending.promise);
    const view = render(<ProfilePage />); await waitFor(() => expect(authMock.getProfile).toHaveBeenCalledOnce());
    testSession.replace('actor'); authState.user = { ...createProfile(), id: 8, fullName: 'Новый пользователь' } as User;
    authMock.getProfile.mockResolvedValue(authState.user); view.rerender(<ProfilePage />);
    await act(async () => pending.resolve(createProfile()));
    expect(screen.queryByDisplayValue('Иван Иванов')).toBeNull();
  });
  it('ownership baseline: does not save a timezone for a replacement login after profile update', async () => {
    const pending = deferred<ReturnType<typeof createProfile>>(); authMock.updateProfile.mockReturnValueOnce(pending.promise);
    render(<ProfilePage />); await screen.findByText('Контактные данные');
    fireEvent.click(screen.getByRole('button', { name: /^Сохранить профиль$/i }));
    await waitFor(() => expect(authMock.updateProfile).toHaveBeenCalledOnce()); testSession.replace();
    await act(async () => pending.resolve(createProfile()));
    expect(apiMock.upsertUserSetting).not.toHaveBeenCalled(); expect(refreshUserMock).not.toHaveBeenCalled(); expect(toastMock.success).not.toHaveBeenCalled();
  });

  const replacement = (mode: string, view: ReturnType<typeof render>) => {
    if (mode === 'unmount') { view.unmount(); return; }
    if (mode === 'role') authState.user = { ...createProfile(), role: { id: 8, name: 'lawyer' } } as User;
    else if (mode === 'permissions') permissionsState.permissions = { ...permissionsState.permissions, calendar: { canView: false, viewScope: 'own' }, audit: { canView: false } };
    else { testSession.replace(mode as 'login' | 'actor' | 'logout'); if (mode === 'actor') authState.user = { ...createProfile(), id: 8, fullName: 'Новый пользователь' } as User; if (mode === 'logout') authState.user = undefined; }
    if (mode !== 'logout') authMock.getProfile.mockResolvedValue(authState.user);
    view.rerender(<ProfilePage />);
  };
  const selectPhoto = async () => {
    const input = document.querySelector<HTMLInputElement>('input[type="file"]')!;
    fireEvent.change(input, { target: { files: [new File(['image'], 'avatar.png', { type: 'image/png' })] } });
    await screen.findByRole('button', { name: 'Подтвердить фото' });
  };
  const perform = async (kind: string) => {
    if (kind === 'profile') { fireEvent.change(screen.getByLabelText('Часовой пояс'), { target: { value: 'Europe/Samara' } }); fireEvent.click(screen.getByRole('button', { name: 'Сохранить профиль' })); }
    if (kind === 'tasks') { fireEvent.click(screen.getByRole('tab', { name: /^Задачи/ })); fireEvent.click(screen.getByRole('button', { name: 'Сохранить' })); }
    if (kind === 'photo') { await selectPhoto(); fireEvent.click(screen.getByRole('button', { name: 'Подтвердить фото' })); }
    if (['password', 'detach', 'link'].includes(kind)) {
      fireEvent.click(screen.getByRole('tab', { name: /^Вход и безопасность/ }));
      if (kind === 'password') { fireEvent.change(screen.getByLabelText('Новый пароль'), { target: { value: 'synthetic-password' } }); fireEvent.change(screen.getByLabelText('Подтверждение нового пароля'), { target: { value: 'synthetic-password' } }); fireEvent.click(screen.getByRole('button', { name: 'Изменить пароль' })); }
      if (kind === 'detach') fireEvent.click(screen.getByRole('button', { name: 'Отвязать' }));
      if (kind === 'link') fireEvent.click(screen.getByRole('button', { name: 'Подключить VK ID' }));
    }
  };
  it.each(['login', 'actor', 'logout', 'role', 'permissions', 'unmount'].flatMap(mode => ['success', 'error'].map(outcome => ({ mode, outcome }))))('drops profile read $outcome after $mode', async ({ mode, outcome }) => {
    const pending = deferred<ReturnType<typeof createProfile>>(); authMock.getProfile.mockReturnValueOnce(pending.promise);
    const view = render(<ProfilePage />); await waitFor(() => expect(authMock.getProfile).toHaveBeenCalledOnce());
    const signal = apiMock.getUserSetting.mock.calls[0][1].signal as AbortSignal;
    replacement(mode, view);
    await act(async () => { if (outcome === 'error') pending.reject(new Error('Старая ошибка')); else pending.resolve({ ...createProfile(), fullName: 'Старый ответ' }); });
    expect(screen.queryByDisplayValue('Старый ответ')).toBeNull(); expect(toastMock.error).not.toHaveBeenCalled(); expect(signal.aborted).toBe(true);
  });
  it.each(['profile', 'tasks', 'password', 'photo', 'detach', 'link'].flatMap(kind => ['login', 'permissions', 'unmount'].flatMap(mode => ['success', 'error'].map(outcome => ({ kind, mode, outcome })))))(
    'drops $kind write $outcome after $mode', async ({ kind, mode, outcome }) => {
      const pending = deferred<unknown>(); const method = kind === 'tasks' ? apiMock.upsertUserSetting : kind === 'profile' ? authMock.updateProfile : kind === 'password' ? authMock.changePassword : kind === 'photo' ? authMock.uploadProfilePhoto : kind === 'detach' ? authMock.detachExternalIdentity : authMock.startExternalAuth;
      method.mockReturnValueOnce(pending.promise);
      const view = render(<ProfilePage />); await screen.findByText('Контактные данные'); await perform(kind); await waitFor(() => expect(method).toHaveBeenCalledOnce());
      vi.mocked(window.open).mockClear(); replacement(mode, view);
      await act(async () => { if (outcome === 'error') pending.reject(new Error('Старая ошибка')); else pending.resolve(kind === 'detach' ? { detached: true, user: { ...createProfile(), fullName: 'Старый ответ' } } : kind === 'link' ? { state: 'old-state', authUrl: 'https://example.test/old' } : { ...createProfile(), fullName: 'Старый ответ' }); });
      expect(screen.queryByDisplayValue('Старый ответ')).toBeNull(); expect(refreshUserMock).not.toHaveBeenCalled(); expect(toastMock.error).not.toHaveBeenCalled(); expect(toastMock.success).not.toHaveBeenCalled(); expect(window.open).not.toHaveBeenCalled();
      if (kind === 'profile') expect(apiMock.upsertUserSetting).not.toHaveBeenCalled();
    });
  it.each(['login', 'actor', 'logout', 'role', 'permissions', 'unmount'])('does not detach after an old confirmation: %s', async mode => {
    const pending = deferred<boolean>(); confirmMock.mockReturnValueOnce(pending.promise);
    const view = render(<ProfilePage />); await screen.findByText('Контактные данные'); await perform('detach');
    await waitFor(() => expect(confirmMock).toHaveBeenCalledOnce()); replacement(mode, view); await act(async () => pending.resolve(true));
    expect(authMock.detachExternalIdentity).not.toHaveBeenCalled(); expect(toastMock.error).not.toHaveBeenCalled();
  });
  it('preserves a manual timezone, never uses ordinary PUT for initialization and accepts HTTP204/null', async () => {
    render(<ProfilePage />); await screen.findByText('Контактные данные');
    expect(apiMock.initializeUserSetting).toHaveBeenCalledExactlyOnceWith('profile.timeZoneId', expect.any(String), { signal: expect.any(AbortSignal) }); expect(apiMock.upsertUserSetting).not.toHaveBeenCalled();
  });
  it.each([new Error('HTTP 403 upstream HTTP 404'), { key: 'other.setting', value: 'UTC' }, undefined, { key: 'profile.timeZoneId', value: [] }])('does not initialize an unconfirmed timezone %j', async value => {
    apiMock.getUserSetting.mockImplementation(async key => { if (key !== 'profile.timeZoneId') return null; if (value instanceof Error) throw value; return value; });
    render(<ProfilePage />); await screen.findByText('Контактные данные'); expect(apiMock.initializeUserSetting).not.toHaveBeenCalled(); expect(apiMock.upsertUserSetting).not.toHaveBeenCalled();
  });
  it('loads the configured timezone without overwriting it', async () => {
    apiMock.getUserSetting.mockImplementation(async key => key === 'profile.timeZoneId' ? { key, value: 'Europe/Samara' } : null);
    render(<ProfilePage />); await screen.findByText('Контактные данные'); expect((screen.getByLabelText('Часовой пояс') as HTMLSelectElement).value).toBe('Europe/Samara'); expect(apiMock.initializeUserSetting).not.toHaveBeenCalled();
  });
  it('accepts a legacy direct HTTP404 only for a missing timezone', async () => {
    apiMock.getUserSetting.mockImplementation(async key => { if (key === 'profile.timeZoneId') throw new Error('HTTP 404 Not Found'); return null; });
    render(<ProfilePage />); await screen.findByText('Контактные данные'); expect(apiMock.initializeUserSetting).toHaveBeenCalledOnce(); expect(apiMock.upsertUserSetting).not.toHaveBeenCalled();
  });
  it('refuses a profile belonging to a different actor and does not enable saving', async () => {
    authMock.getProfile.mockResolvedValue({ ...createProfile(), id: 99, fullName: 'Чужой профиль' });
    render(<ProfilePage />); await waitFor(() => expect(toastMock.error).toHaveBeenCalledOnce()); expect(screen.queryByDisplayValue('Чужой профиль')).toBeNull();
    expect((screen.getByRole('button', { name: 'Сохранить профиль' }) as HTMLButtonElement).disabled).toBe(true); expect(apiMock.initializeUserSetting).not.toHaveBeenCalled();
  });
  it('shows a current load error and allows an explicit refresh', async () => {
    authMock.getProfile.mockRejectedValueOnce(new Error('Ошибка чтения'));
    render(<ProfilePage />); await waitFor(() => expect(toastMock.error).toHaveBeenCalledOnce()); fireEvent.click(screen.getByRole('button', { name: 'Обновить данные' }));
    await screen.findByDisplayValue('Иван Иванов'); expect(authMock.getProfile).toHaveBeenCalledTimes(2);
  });
  it('does not reload or lose edits on token refresh', async () => {
    const view = render(<ProfilePage />); await screen.findByText('Контактные данные'); fireEvent.change(screen.getByLabelText('Полное имя'), { target: { value: 'Мой новый текст' } });
    testSession.refresh(); view.rerender(<ProfilePage />); await act(async () => {}); expect(screen.getByDisplayValue('Мой новый текст')).toBeTruthy(); expect(authMock.getProfile).toHaveBeenCalledOnce();
  });
  it('uses only the latest manual refresh and catches its errors', async () => {
    render(<ProfilePage />); await screen.findByText('Контактные данные'); const old = deferred<ReturnType<typeof createProfile>>(); authMock.getProfile.mockReturnValueOnce(old.promise).mockResolvedValueOnce({ ...createProfile(), fullName: 'Свежий профиль' });
    fireEvent.click(screen.getByRole('button', { name: 'Обновить данные' })); fireEvent.click(screen.getByRole('button', { name: 'Обновить данные' })); await screen.findByDisplayValue('Свежий профиль');
    await act(async () => old.reject(new Error('Старая ошибка'))); expect(screen.getByDisplayValue('Свежий профиль')).toBeTruthy(); expect(toastMock.error).not.toHaveBeenCalled();
  });
  it('does not overwrite edits made while a refresh was pending', async () => {
    render(<ProfilePage />); await screen.findByText('Контактные данные'); const pending = deferred<ReturnType<typeof createProfile>>(); authMock.getProfile.mockReturnValueOnce(pending.promise);
    fireEvent.click(screen.getByRole('button', { name: 'Обновить данные' })); fireEvent.change(screen.getByLabelText('Полное имя'), { target: { value: 'Набранный текст' } }); await act(async () => pending.resolve({ ...createProfile(), fullName: 'Серверный текст' }));
    expect(screen.getByDisplayValue('Набранный текст')).toBeTruthy(); expect(screen.queryByDisplayValue('Серверный текст')).toBeNull();
  });
  it('cancels pre-write reads and preserves text entered after save started', async () => {
    render(<ProfilePage />); await screen.findByText('Контактные данные'); const old = deferred<ReturnType<typeof createProfile>>(); const write = deferred<ReturnType<typeof createProfile>>(); authMock.getProfile.mockReturnValueOnce(old.promise); authMock.updateProfile.mockReturnValueOnce(write.promise);
    fireEvent.click(screen.getByRole('button', { name: 'Обновить данные' })); const signal = apiMock.getUserSetting.mock.calls.at(-1)![1].signal as AbortSignal;
    await perform('profile'); fireEvent.change(screen.getByLabelText('Полное имя'), { target: { value: 'Следующая правка' } }); await act(async () => write.resolve({ ...createProfile(), fullName: 'Принятый текст' }));
    expect(signal.aborted).toBe(true); expect(screen.getByDisplayValue('Следующая правка')).toBeTruthy(); await act(async () => old.resolve({ ...createProfile(), fullName: 'Старое чтение' })); expect(screen.getByDisplayValue('Следующая правка')).toBeTruthy(); expect(toastMock.success).toHaveBeenCalledWith('Профиль обновлён.');
  });
  it('keeps a duplicate save blocked through timezone persistence and user refresh', async () => {
    const preference = deferred<unknown>(); const refreshed = deferred<void>(); apiMock.upsertUserSetting.mockReturnValueOnce(preference.promise); refreshUserMock.mockReturnValueOnce(refreshed.promise);
    render(<ProfilePage />); await screen.findByText('Контактные данные'); await perform('profile'); await waitFor(() => expect(apiMock.upsertUserSetting).toHaveBeenCalledOnce()); await perform('profile'); expect(authMock.updateProfile).toHaveBeenCalledOnce();
    await act(async () => preference.resolve({ key: 'profile.timeZoneId', value: 'UTC' })); await perform('profile'); expect(authMock.updateProfile).toHaveBeenCalledOnce(); expect(toastMock.success).not.toHaveBeenCalled();
    await act(async () => refreshed.resolve()); expect(toastMock.success).toHaveBeenCalledOnce(); expect((screen.getByRole('button', { name: 'Сохранить профиль' }) as HTMLButtonElement).disabled).toBe(false);
  });
  it('keeps a same-user reservation across role changes and refreshes after the old write settles', async () => {
    const pending = deferred<ReturnType<typeof createProfile>>(); authMock.updateProfile.mockReturnValueOnce(pending.promise); const view = render(<ProfilePage />); await screen.findByText('Контактные данные'); await perform('profile');
    replacement('role', view); await screen.findByText('Контактные данные'); expect((screen.getByRole('button', { name: 'Сохранить профиль' }) as HTMLButtonElement).disabled).toBe(true);
    authMock.getProfile.mockResolvedValue({ ...createProfile(), fullName: 'Сохранённый профиль' }); await act(async () => pending.resolve(createProfile())); await screen.findByDisplayValue('Сохранённый профиль');
    expect(authMock.updateProfile).toHaveBeenCalledOnce(); expect(toastMock.success).not.toHaveBeenCalled(); expect((screen.getByRole('button', { name: 'Сохранить профиль' }) as HTMLButtonElement).disabled).toBe(false);
  });
  it.each(['timezone', 'refresh'])('reports a partial save accurately when %s fails', async stage => {
    authMock.updateProfile.mockResolvedValue({ ...createProfile(), fullName: 'Сохранённое имя' });
    if (stage === 'timezone') apiMock.upsertUserSetting.mockRejectedValueOnce(new Error('Нет связи')); else refreshUserMock.mockRejectedValueOnce(new Error('Нет связи'));
    render(<ProfilePage />); await screen.findByText('Контактные данные'); await perform('profile'); await waitFor(() => expect(toastMock.error).toHaveBeenCalledOnce());
    expect(screen.getByDisplayValue('Сохранённое имя')).toBeTruthy(); expect(toastMock.error.mock.calls[0][0]).toMatch(stage === 'timezone' ? /профиля сохранены, но часовой пояс не сохранён/ : /профиля и часовой пояс сохранены/); expect(toastMock.success).not.toHaveBeenCalled(); expect(apiMock.upsertUserSetting).toHaveBeenCalledOnce();
  });
  it('does not send follow-up writes after a mismatching profile write response', async () => {
    authMock.updateProfile.mockResolvedValue({ ...createProfile(), id: 99 }); render(<ProfilePage />); await screen.findByText('Контактные данные'); await perform('profile'); await waitFor(() => expect(toastMock.error).toHaveBeenCalledOnce()); expect(apiMock.upsertUserSetting).not.toHaveBeenCalled(); expect(refreshUserMock).not.toHaveBeenCalled();
  });

  it.each(['profile', 'tasks', 'password', 'photo', 'detach', 'link'])('completes the current %s operation', async kind => {
    render(<ProfilePage />); await screen.findByText('Контактные данные'); await perform(kind);
    if (kind === 'link') { await waitFor(() => expect(window.open).toHaveBeenCalledExactlyOnceWith('https://example.test/auth', 'pp-profile-external-link', 'popup=yes,width=540,height=720')); }
    else { await waitFor(() => expect(toastMock.success).toHaveBeenCalledOnce()); }
    if (kind === 'tasks') expect(apiMock.upsertUserSetting).toHaveBeenCalledExactlyOnceWith('tasks.notificationDefaults.v1', JSON.stringify({ reminders: ['before1h', 'atDue'], channels: ['inApp'] }));
    if (kind === 'password') { expect(authMock.changePassword).toHaveBeenCalledExactlyOnceWith({ currentPassword: undefined, newPassword: 'synthetic-password' }); expect((screen.getByLabelText('Новый пароль') as HTMLInputElement).value).toBe(''); }
    if (kind === 'photo') { expect(authMock.uploadProfilePhoto).toHaveBeenCalledOnce(); expect(screen.queryByRole('button', { name: 'Подтвердить фото' })).toBeNull(); }
    if (kind === 'detach') { expect(confirmMock).toHaveBeenCalledOnce(); expect(authMock.detachExternalIdentity).toHaveBeenCalledExactlyOnceWith('google'); }
  });
  it.each(['profile', 'tasks', 'password', 'photo', 'detach', 'link'])('shows the current %s error without automatic retry', async kind => {
    const method = kind === 'tasks' ? apiMock.upsertUserSetting : kind === 'profile' ? authMock.updateProfile : kind === 'password' ? authMock.changePassword : kind === 'photo' ? authMock.uploadProfilePhoto : kind === 'detach' ? authMock.detachExternalIdentity : authMock.startExternalAuth;
    method.mockRejectedValueOnce(new Error('Текущий отказ'));
    render(<ProfilePage />); await screen.findByText('Контактные данные'); await perform(kind); await waitFor(() => expect(toastMock.error).toHaveBeenCalledOnce());
    expect(method).toHaveBeenCalledOnce(); expect(toastMock.success).not.toHaveBeenCalled(); expect(refreshUserMock).not.toHaveBeenCalled();
  });
  it('respects a cancelled detach confirmation and releases the reservation', async () => {
    confirmMock.mockResolvedValueOnce(false); render(<ProfilePage />); await screen.findByText('Контактные данные'); await perform('detach');
    await waitFor(() => expect((screen.getByRole('button', { name: 'Отвязать' }) as HTMLButtonElement).disabled).toBe(false)); expect(authMock.detachExternalIdentity).not.toHaveBeenCalled(); expect(toastMock.success).not.toHaveBeenCalled();
  });
  it.each(['preference', 'refresh'])('drops a late %s stage after login replacement', async stage => {
    const pending = deferred<unknown>(); if (stage === 'preference') apiMock.upsertUserSetting.mockReturnValueOnce(pending.promise); else refreshUserMock.mockReturnValueOnce(pending.promise);
    render(<ProfilePage />); await screen.findByText('Контактные данные'); await perform('profile');
    await waitFor(() => expect(stage === 'preference' ? apiMock.upsertUserSetting : refreshUserMock).toHaveBeenCalledOnce()); testSession.replace();
    await act(async () => pending.resolve(undefined)); expect(toastMock.success).not.toHaveBeenCalled(); expect(toastMock.error).not.toHaveBeenCalled();
    if (stage === 'preference') expect(refreshUserMock).not.toHaveBeenCalled();
  });
  it('does not write an unchanged timezone when saving contacts after preference read failure', async () => {
    apiMock.getUserSetting.mockRejectedValue(new Error('HTTP 403'));
    render(<ProfilePage />); await screen.findByText('Контактные данные'); expect(screen.getByText(/Часовой пояс не загрузился/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить профиль' })); await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith('Профиль обновлён.'));
    expect(apiMock.upsertUserSetting).not.toHaveBeenCalled(); expect(apiMock.initializeUserSetting).not.toHaveBeenCalled();
  });
  it('allows an explicit timezone choice after preference read failure', async () => {
    apiMock.getUserSetting.mockRejectedValue(new Error('HTTP 403'));
    render(<ProfilePage />); await screen.findByText('Контактные данные'); await perform('profile'); await waitFor(() => expect(toastMock.success).toHaveBeenCalledOnce());
    expect(apiMock.upsertUserSetting).toHaveBeenCalledExactlyOnceWith('profile.timeZoneId', 'Europe/Samara'); expect(screen.queryByText(/Часовой пояс не загрузился/)).toBeNull();
  });
  it('requires an explicit reminder selection after a failed read', async () => {
    apiMock.getUserSetting.mockImplementation(async key => { if (key === 'tasks.notificationDefaults.v1') throw new Error('HTTP 403'); return { key, value: 'Europe/Samara' }; });
    render(<ProfilePage />); await screen.findByText('Контактные данные'); fireEvent.click(screen.getByRole('tab', { name: /^Задачи/ }));
    expect(screen.getByText(/Напоминания не загрузились/)).toBeTruthy(); expect((screen.getByRole('button', { name: 'Сохранить' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Выбрать напоминания' })); fireEvent.click(screen.getByRole('button', { name: 'Сохранить' })); await waitFor(() => expect(toastMock.success).toHaveBeenCalledOnce());
    expect(apiMock.upsertUserSetting).toHaveBeenCalledExactlyOnceWith('tasks.notificationDefaults.v1', JSON.stringify({ reminders: ['atDue'], channels: ['inApp'] })); expect(screen.queryByText(/Напоминания не загрузились/)).toBeNull();
  });
  it('does not issue private reads for a guest', async () => {
    testSession.replace('logout'); authState.user = undefined; render(<ProfilePage />); await act(async () => {}); expect(authMock.getProfile).not.toHaveBeenCalled(); expect(apiMock.getUserSetting).not.toHaveBeenCalled();
  });
  it('keeps strict-mode initialization safe and the current owner usable', async () => {
    render(<StrictMode><ProfilePage /></StrictMode>); await screen.findByText('Контактные данные'); await perform('profile'); await waitFor(() => expect(toastMock.success).toHaveBeenCalledOnce()); expect(authMock.updateProfile).toHaveBeenCalledOnce();
  });
  it('does not upload after a closed crop finishes preparing its file', async () => {
    render(<ProfilePage />); await screen.findByText('Контактные данные'); await selectPhoto(); const old = cropMock.latest!;
    fireEvent.click(screen.getByRole('button', { name: 'Закрыть кадрирование' })); await act(async () => old.onConfirm(new File(['old'], 'old.png')));
    expect(authMock.uploadProfilePhoto).not.toHaveBeenCalled();
  });
  it('does not let an old file read reopen the crop after a newer selection', async () => {
    const readers: Array<{ onload: (() => void) | null; result: string; readAsDataURL: () => void }> = [];
    vi.stubGlobal('FileReader', class { onload = null; onerror = null; result = 'data:image/png;base64,YQ=='; constructor() { readers.push(this); } readAsDataURL() {} });
    render(<ProfilePage />); await screen.findByText('Контактные данные'); const input = document.querySelector<HTMLInputElement>('input[type="file"]')!;
    fireEvent.change(input, { target: { files: [new File(['a'], 'old.png')] } }); fireEvent.change(input, { target: { files: [new File(['b'], 'new.png')] } });
    await act(async () => readers[1].onload?.()); const current = cropMock.latest; await act(async () => readers[0].onload?.()); expect(cropMock.latest).toBe(current); expect(authMock.uploadProfilePhoto).not.toHaveBeenCalled();
  });
  it('keeps the last password edits when an earlier password save completes', async () => {
    const pending = deferred<ReturnType<typeof createProfile>>(); authMock.changePassword.mockReturnValueOnce(pending.promise);
    render(<ProfilePage />); await screen.findByText('Контактные данные'); await perform('password'); fireEvent.change(screen.getByLabelText('Новый пароль'), { target: { value: 'next-synthetic' } }); await act(async () => pending.resolve(createProfile()));
    expect((screen.getByLabelText('Новый пароль') as HTMLInputElement).value).toBe('next-synthetic');
  });
  function capturePoll() {
    let poll: (() => Promise<void>) | undefined;
    vi.spyOn(window, 'setInterval').mockImplementation(callback => { poll = callback as () => Promise<void>; return 191 as unknown as ReturnType<typeof window.setInterval>; });
    vi.spyOn(window, 'clearInterval').mockImplementation(() => {});
    return () => { if (!poll) throw new Error('Expected external identity polling'); return poll(); };
  }
  it('completes external linking through an owned nonoverlapping poll and refresh', async () => {
    const poll = capturePoll(); const pending = deferred<unknown>(); authMock.getExternalAuthStatus.mockReturnValueOnce(pending.promise);
    render(<ProfilePage />); await screen.findByText('Контактные данные'); await perform('link'); await waitFor(() => expect(window.open).toHaveBeenCalledOnce());
    let running: Promise<void>; await act(async () => { running = poll(); }); await act(async () => { await poll(); }); expect(authMock.getExternalAuthStatus).toHaveBeenCalledOnce();
    await act(async () => { pending.resolve({ completed: true, linked: true, provider: 'vk', status: 'completed' }); await running!; });
    expect(refreshUserMock).toHaveBeenCalledOnce(); expect(toastMock.success).toHaveBeenCalledWith('VK ID успешно привязан к профилю.'); expect(window.clearInterval).toHaveBeenCalled();
  });
  it.each(['success', 'error'])('drops a pending external status %s after login replacement', async outcome => {
    const poll = capturePoll(); const pending = deferred<unknown>(); authMock.getExternalAuthStatus.mockReturnValueOnce(pending.promise);
    render(<ProfilePage />); await screen.findByText('Контактные данные'); await perform('link'); await waitFor(() => expect(window.open).toHaveBeenCalledOnce());
    let running: Promise<void>; await act(async () => { running = poll(); }); testSession.replace();
    await act(async () => { if (outcome === 'error') pending.reject(new Error('Старый статус')); else pending.resolve({ completed: true, linked: true, provider: 'vk' }); await running!; });
    expect(refreshUserMock).not.toHaveBeenCalled(); expect(authMock.getProfile).toHaveBeenCalledTimes(2); expect(toastMock.success).not.toHaveBeenCalled(); expect(toastMock.error).not.toHaveBeenCalled();
  });
  it('does not consume an external status while another profile write is running', async () => {
    const poll = capturePoll(); const pending = deferred<ReturnType<typeof createProfile>>(); authMock.updateProfile.mockReturnValueOnce(pending.promise); authMock.getExternalAuthStatus.mockResolvedValue({ status: 'pending', completed: false });
    render(<ProfilePage />); await screen.findByText('Контактные данные'); await perform('link'); await waitFor(() => expect(window.open).toHaveBeenCalledOnce()); fireEvent.click(screen.getByRole('tab', { name: /^Профиль/ })); await perform('profile'); await act(async () => { await poll(); }); expect(authMock.getExternalAuthStatus).not.toHaveBeenCalled();
    await act(async () => pending.resolve(createProfile())); await act(async () => { await poll(); }); expect(authMock.getExternalAuthStatus).toHaveBeenCalledOnce();
  });
  it('does not adopt the URL callback in a replacement login', async () => {
    window.history.replaceState({}, '', '/?externalAuthState=old-state&externalAuthProvider=google'); const poll = capturePoll(); authMock.getExternalAuthStatus.mockResolvedValue({ status: 'pending', completed: false });
    const view = render(<ProfilePage />); await screen.findByText('Контактные данные'); replacement('login', view); await screen.findByText('Контактные данные'); await act(async () => { await poll(); }); expect(authMock.getExternalAuthStatus).not.toHaveBeenCalled();
  });

  it('merges a fresh profile after a stale write without losing existing edits in the replacement screen', async () => {
    const pending = deferred<ReturnType<typeof createProfile>>(); authMock.updateProfile.mockReturnValueOnce(pending.promise);
    const view = render(<ProfilePage />); await screen.findByText('Контактные данные'); await perform('profile'); replacement('role', view); await screen.findByText('Контактные данные');
    fireEvent.change(screen.getByLabelText('Полное имя'), { target: { value: 'Текст новой формы' } }); authMock.getProfile.mockResolvedValue({ ...createProfile(), phoneNumber: '+79991112233' });
    await act(async () => pending.resolve(createProfile())); await screen.findByDisplayValue('+79991112233'); expect(screen.getByDisplayValue('Текст новой формы')).toBeTruthy();
  });
  it('keeps existing contact drafts when a photo response refreshes the profile', async () => {
    authMock.uploadProfilePhoto.mockResolvedValue({ ...createProfile(), phoneNumber: '+79991112233' });
    render(<ProfilePage />); await screen.findByText('Контактные данные'); fireEvent.change(screen.getByLabelText('Полное имя'), { target: { value: 'Мой черновик' } }); await perform('photo'); await waitFor(() => expect(toastMock.success).toHaveBeenCalledOnce());
    expect(screen.getByDisplayValue('Мой черновик')).toBeTruthy(); expect(screen.getByDisplayValue('+79991112233')).toBeTruthy();
  });

});
