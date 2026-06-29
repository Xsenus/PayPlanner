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

const permissionsState = vi.hoisted(() => ({
  permissions: {
    calendar: { canView: true, viewScope: 'own' },
    audit: { canView: true },
    payments: { canView: true },
  },
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
  ProfilePhotoCropModal: () => null,
}));

vi.mock('../Tasks/TaskNotificationDefaultsPanel', () => ({
  TaskNotificationDefaultsPanel: () => <div>Настройки напоминаний mock</div>,
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
    vi.clearAllMocks();
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
    authMock.getProfile.mockResolvedValueOnce(createProfile({ roleName: 'lawyer' }));

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
    authMock.getProfile.mockResolvedValueOnce(createProfile({ roleName: 'lawyer' }));

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
          paidDate: null,
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

    expect(await screen.findByText('Waiting contractor task')).toBeTruthy();
    expect(screen.getByText('Result contractor task')).toBeTruthy();
    expect(screen.getByText('Sixth visible task')).toBeTruthy();
    expect(screen.getByText('Paid contractor task')).toBeTruthy();
    expect(screen.getByText('Client One')).toBeTruthy();
    expect(screen.getByText('Case One')).toBeTruthy();
    expect(screen.getByText(/#1001/)).toBeTruthy();
    expect(screen.getByText(/#1004/)).toBeTruthy();
  });
});
