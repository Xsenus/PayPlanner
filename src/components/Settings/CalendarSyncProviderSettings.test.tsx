import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { CalendarSyncProviderSettings } from './CalendarSyncProviderSettings';

const apiMock = vi.hoisted(() => ({
  getCalendarSyncProviders: vi.fn(),
  getCalendarSyncReadiness: vi.fn(),
  getCalendarSyncSettings: vi.fn(),
  getCalendarSyncLogs: vi.fn(),
  updateCalendarSyncSettings: vi.fn(),
}));

const authMock = vi.hoisted(() => ({
  getUsers: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  apiService: apiMock,
}));

vi.mock('../../services/authService', () => ({
  authService: authMock,
}));

describe('CalendarSyncProviderSettings', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    apiMock.getCalendarSyncProviders.mockResolvedValue([
      {
        key: 'ics-export',
        label: 'ICS-файлы',
        kind: 'ics',
        status: 'active',
        isAvailable: true,
        featureFlagEnabled: true,
        requiresOAuth: false,
        supportsManualExport: true,
        supportsBackgroundSync: false,
        supportsTwoWaySync: false,
        supportedTargets: ['Календарь задач', 'Календарь дел'],
        message: 'ICS доступен.',
      },
      {
        key: 'google-calendar',
        label: 'Google Calendar',
        kind: 'google',
        status: 'requires-setup',
        isAvailable: false,
        featureFlagEnabled: true,
        requiresOAuth: true,
        supportsManualExport: false,
        supportsBackgroundSync: true,
        supportsTwoWaySync: true,
        supportedTargets: ['Календарь задач'],
        message: 'Нужна настройка OAuth.',
      },
    ]);
    apiMock.getCalendarSyncReadiness.mockResolvedValue({
      generatedAt: '2026-05-26T16:00:00Z',
      externalCalendarSyncEnabled: true,
      googleCalendarSyncEnabled: true,
      yandexCalendarSyncEnabled: false,
      readyToConnect: false,
      items: [
        {
          key: 'ics-fallback',
          label: 'ICS-выгрузка',
          status: 'ready',
          isReady: true,
          isRequired: false,
          severity: 'success',
          message: 'ICS остается доступен.',
        },
        {
          key: 'feature-flag',
          label: 'Google Calendar в админке',
          status: 'ready',
          isReady: true,
          isRequired: true,
          severity: 'success',
          message: 'Включено.',
        },
        {
          key: 'conflict-policy',
          label: 'Правила конфликтов',
          status: 'pending',
          isReady: false,
          isRequired: true,
          severity: 'warning',
          message: 'Нужно утвердить модель конфликтов.',
        },
      ],
    });
    apiMock.getCalendarSyncSettings.mockResolvedValue({
      externalCalendarSyncEnabled: false,
      googleCalendarSyncEnabled: false,
      yandexCalendarSyncEnabled: false,
      googleSyncIntervalMinutes: 10,
      yandexSyncIntervalMinutes: 10,
      syncMode: 'notConfigured',
      conflictResolutionMode: 'manualConfirmation',
      userControlMode: 'userManaged',
      defaultSyncFromDate: null,
      inactiveUserAction: 'cleanupExternalData',
      requireManualConfirmation: true,
      dryRunConfirmed: false,
      notes: '',
      updatedAt: null,
    });
    authMock.getUsers.mockResolvedValue([
      {
        id: 5,
        email: 'test@example.com',
        fullName: 'Тестовый пользователь',
        isEmployee: true,
        role: { id: 1, name: 'admin' },
        isActive: true,
        isApproved: true,
        createdAt: '2026-05-26T10:00:00Z',
      },
    ]);
    apiMock.getCalendarSyncLogs.mockResolvedValue({
      items: [
        {
          provider: 'google-calendar',
          providerLabel: 'Google Calendar',
          id: 1,
          connectionId: 10,
          userId: 5,
          userName: 'Тестовый пользователь',
          userEmail: 'test@example.com',
          taskJournalItemId: 42,
          calendarOwnerUserId: 5,
          taskAssigneeUserId: 5,
          taskTitle: 'Проверить документы',
          taskObjectName: 'Дело клиента',
          taskAssigneeName: 'Иван Исполнитель',
          calendarName: 'PayPlanner - Тестовый пользователь',
          calendarOwnerName: 'Тестовый пользователь',
          calendarOwnerEmail: 'owner@example.com',
          targetType: 'task',
          targetLabel: 'Задача: Проверить документы',
          direction: 'crm-to-google',
          directionLabel: 'CRM → Google Calendar',
          operation: 'create-event',
          operationLabel: 'Создание события',
          status: 'success',
          statusLabel: 'Успешно',
          message: 'Событие создано.',
          details: 'Синхронизация выполнена для тестового пользователя.',
          changeSummary: '',
          actorLabel: 'Тестовый пользователь (test@example.com)',
          calendarRoleLabel: 'Календарь исполнителя: Тестовый пользователь (owner@example.com)',
          createdAt: '2026-05-26T17:20:00Z',
        },
      ],
      total: 1,
      page: 1,
      pageSize: 25,
    });
    apiMock.updateCalendarSyncSettings.mockImplementation(async (payload) => ({
      externalCalendarSyncEnabled: payload.googleCalendarSyncEnabled || payload.yandexCalendarSyncEnabled,
      googleCalendarSyncEnabled: payload.googleCalendarSyncEnabled,
      yandexCalendarSyncEnabled: payload.yandexCalendarSyncEnabled,
      googleSyncIntervalMinutes: payload.googleSyncIntervalMinutes,
      yandexSyncIntervalMinutes: payload.yandexSyncIntervalMinutes,
      syncMode: payload.syncMode,
      conflictResolutionMode: payload.conflictResolutionMode,
      userControlMode: payload.userControlMode,
      defaultSyncFromDate: payload.defaultSyncFromDate,
      inactiveUserAction: payload.inactiveUserAction,
      requireManualConfirmation: payload.requireManualConfirmation,
      dryRunConfirmed: payload.dryRunConfirmed,
      notes: payload.notes,
      updatedAt: '2026-05-26T17:10:00Z',
    }));
  });

  it('shows provider diagnostics and readiness checklist', async () => {
    render(<CalendarSyncProviderSettings />);

    expect(await screen.findByText('Провайдеры календарной синхронизации')).toBeTruthy();
    expect(screen.getByText('ICS-файлы')).toBeTruthy();
    expect(screen.getAllByText('Google Calendar').length).toBeGreaterThan(0);
    expect(screen.getByText('Модель синхронизации и конфликты')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Показать готовность к подключению календарей' }));
    expect(screen.getByText('Готовность к подключению Google/Яндекс')).toBeTruthy();
    expect(screen.getByText('Выполнено обязательных шагов: 1 из 2. Пока все обязательные пункты не закрыты, внешняя синхронизация остается подготовительным контуром.')).toBeTruthy();
    expect(screen.getByText('Правила конфликтов')).toBeTruthy();
    expect(screen.getByText('Нужна подготовка')).toBeTruthy();
    expect(screen.getByText('Задача: Проверить документы')).toBeTruthy();
    expect(screen.getAllByText('Исполнитель задачи: Иван Исполнитель').length).toBeGreaterThan(0);
    expect(screen.getByText('Календарь исполнителя: Тестовый пользователь (owner@example.com)')).toBeTruthy();
    expect(screen.getByText('Дело клиента')).toBeTruthy();
  });

  it('saves calendar sync conflict settings', async () => {
    render(<CalendarSyncProviderSettings />);

    await screen.findByText('Модель синхронизации и конфликты');
    fireEvent.click(screen.getAllByLabelText(/Включить Google Calendar/i)[0]);
    fireEvent.change(screen.getAllByRole('combobox')[0], { target: { value: 'hybrid' } });
    fireEvent.click(screen.getAllByLabelText(/Dry-run на копии базы выполнен/i)[0]);
    fireEvent.change(screen.getByPlaceholderText(/Например: модель согласована/i), { target: { value: 'Согласовано с руководителем.' } });
    fireEvent.click(screen.getByRole('button', { name: /Сохранить/i }));

    await waitFor(() => expect(apiMock.updateCalendarSyncSettings).toHaveBeenCalledWith({
      googleCalendarSyncEnabled: true,
      yandexCalendarSyncEnabled: false,
      googleSyncIntervalMinutes: 10,
      yandexSyncIntervalMinutes: 10,
      syncMode: 'hybrid',
      conflictResolutionMode: 'manualConfirmation',
      userControlMode: 'userManaged',
      defaultSyncFromDate: null,
      inactiveUserAction: 'cleanupExternalData',
      requireManualConfirmation: true,
      dryRunConfirmed: true,
      notes: 'Согласовано с руководителем.',
    }));
    await waitFor(() => expect(apiMock.getCalendarSyncProviders).toHaveBeenCalledTimes(2));
    expect(await screen.findByText('Настройки внешних календарей сохранены.')).toBeTruthy();
  });

  it('opens calendar sync settings explanation modal', async () => {
    render(<CalendarSyncProviderSettings />);

    await screen.findByText('Модель синхронизации и конфликты');
    fireEvent.click(screen.getByRole('button', { name: 'Что делает настройка «Главный источник событий»' }));

    expect(screen.getByText('Пояснение настройки')).toBeTruthy();
    expect(screen.getAllByText('Главный источник событий').length).toBeGreaterThan(1);
    expect(screen.getByText('Определяет, что считать основным источником при обмене данными: CRM, внешний календарь или гибридную модель.')).toBeTruthy();
  });

  it('opens Google Calendar setup instruction from provider help', async () => {
    render(<CalendarSyncProviderSettings />);

    await screen.findByText('Модель синхронизации и конфликты');
    fireEvent.click(screen.getByRole('button', { name: 'Что делает настройка «Включить Google Calendar»' }));

    expect(screen.getByText('Как настроить')).toBeTruthy();
    expect(screen.getByText('Google Cloud Console: Credentials')).toBeTruthy();
    expect(screen.getByText('Redirect URI для кабинета провайдера')).toBeTruthy();
    expect(screen.getByText('https://api.elitpravo.com/api/google-calendar/auth/callback')).toBeTruthy();
    expect(screen.getByText(/Redirect URI должен совпадать полностью/i)).toBeTruthy();
  });

  it('shows Yandex Calendar redirect URI in provider help', async () => {
    render(<CalendarSyncProviderSettings />);

    await screen.findByText('Модель синхронизации и конфликты');
    fireEvent.click(screen.getByRole('button', { name: 'Что делает настройка «Включить Яндекс Календарь»' }));

    expect(screen.getByText('Redirect URI для кабинета провайдера')).toBeTruthy();
    expect(screen.getByText('https://api.elitpravo.com/api/yandex-calendar/auth/callback')).toBeTruthy();
    expect(screen.getByText('Яндекс OAuth: управление приложениями')).toBeTruthy();
  });

  it('loads another page of calendar sync logs', async () => {
    apiMock.getCalendarSyncLogs.mockImplementation(async ({ page, pageSize }) => ({
      items: [
        {
          provider: 'google-calendar',
          providerLabel: 'Google Calendar',
          id: page,
          connectionId: 10,
          userId: 5,
          userName: 'Тестовый пользователь',
          userEmail: 'test@example.com',
          taskJournalItemId: 42,
          calendarOwnerUserId: 5,
          taskAssigneeUserId: 5,
          taskTitle: page === 2 ? 'Вторая страница' : 'Первая страница',
          taskObjectName: 'Дело клиента',
          taskAssigneeName: 'Тестовый пользователь',
          calendarName: 'PayPlanner - Тестовый пользователь',
          calendarOwnerName: 'Тестовый пользователь',
          calendarOwnerEmail: 'owner@example.com',
          targetType: 'task',
          targetLabel: page === 2 ? 'Задача: Вторая страница' : 'Задача: Первая страница',
          direction: 'crm-to-google',
          directionLabel: 'CRM → Google Calendar',
          operation: 'update-event',
          operationLabel: 'Обновление события',
          status: 'success',
          statusLabel: 'Успешно',
          message: 'Событие обновлено.',
          details: '',
          changeSummary: '',
          actorLabel: 'Тестовый пользователь (test@example.com)',
          calendarRoleLabel: 'Календарь исполнителя: Тестовый пользователь (owner@example.com)',
          createdAt: '2026-05-26T17:20:00Z',
        },
      ],
      total: 26,
      page,
      pageSize,
    }));

    render(<CalendarSyncProviderSettings />);

    expect(await screen.findByText('Задача: Первая страница')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Вперёд/i }));

    await waitFor(() => expect(apiMock.getCalendarSyncLogs).toHaveBeenLastCalledWith({ page: 2, pageSize: 25 }));
    expect(await screen.findByText('Задача: Вторая страница')).toBeTruthy();
  });

  it('loads calendar sync logs with selected filters', async () => {
    render(<CalendarSyncProviderSettings />);

    await screen.findByText('Фильтры журнала');
    fireEvent.change(screen.getByLabelText('Провайдер'), { target: { value: 'google-calendar' } });
    fireEvent.change(screen.getByLabelText('Сотрудник'), { target: { value: '5' } });
    fireEvent.change(screen.getByLabelText('Действие'), { target: { value: 'update-task' } });
    fireEvent.change(screen.getByLabelText('Статус'), { target: { value: 'success' } });
    fireEvent.change(screen.getByLabelText('С даты и времени'), { target: { value: '2026-05-26T10:00' } });
    fireEvent.change(screen.getByLabelText('По дату и время'), { target: { value: '2026-05-26T18:00' } });
    fireEvent.change(screen.getByLabelText('Поиск'), { target: { value: 'документы' } });
    fireEvent.click(screen.getByRole('button', { name: 'Применить фильтры' }));

    await waitFor(() => expect(apiMock.getCalendarSyncLogs).toHaveBeenLastCalledWith(expect.objectContaining({
      provider: 'google-calendar',
      status: 'success',
      operation: 'update-task',
      userId: 5,
      search: 'документы',
      page: 1,
      pageSize: 25,
    })));
  });

  it('shows a readable loading error', async () => {
    apiMock.getCalendarSyncProviders.mockRejectedValue(new Error('calendar api down'));

    render(<CalendarSyncProviderSettings />);

    expect(await screen.findByText(/calendar api down/i)).toBeTruthy();
  });
});
