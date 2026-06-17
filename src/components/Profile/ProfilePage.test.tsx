import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ProfilePage } from './ProfilePage';

const authMock = vi.hoisted(() => ({
  getProfile: vi.fn(),
  getAuthProviders: vi.fn(),
  getContractorBalance: vi.fn(),
  attachExternalIdentity: vi.fn(),
  detachExternalIdentity: vi.fn(),
  updateProfile: vi.fn(),
  changePassword: vi.fn(),
}));

const apiMock = vi.hoisted(() => ({
  getUserSetting: vi.fn(),
  upsertUserSetting: vi.fn(),
}));

const refreshUserMock = vi.hoisted(() => vi.fn());
const toastMock = vi.hoisted(() => ({
  error: vi.fn(),
  success: vi.fn(),
}));

vi.mock('../../services/authService', () => ({
  authService: authMock,
}));

vi.mock('../../services/api', () => ({
  apiService: apiMock,
}));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    refreshUser: refreshUserMock,
  }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({
    confirm: vi.fn(),
  }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => toastMock,
}));

vi.mock('./GoogleCalendarPanel', () => ({
  GoogleCalendarPanel: () => <div>Google Calendar mock</div>,
}));

vi.mock('./YandexCalendarPanel', () => ({
  YandexCalendarPanel: () => <div>Яндекс.Календарь mock</div>,
}));

vi.mock('./ProfilePhotoCropModal', () => ({
  ProfilePhotoCropModal: () => null,
}));

vi.mock('../Tasks/TaskNotificationDefaultsPanel', () => ({
  TaskNotificationDefaultsPanel: () => <div>Настройки напоминаний mock</div>,
}));

vi.mock('../ui/ObjectAuditModal', () => ({
  ObjectAuditLauncher: ({ triggerTitle }: { triggerTitle: string }) => <div>{triggerTitle}</div>,
}));

function createProfile() {
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
    role: { name: 'admin' },
    isExternalContractor: false,
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
    vi.clearAllMocks();
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
});
