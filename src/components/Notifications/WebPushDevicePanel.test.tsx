import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { WebPushDevicePanel } from './WebPushDevicePanel';
import type { WebPushStatus } from '../../services/webPushNotifications';

const webPushMock = vi.hoisted(() => ({
  getWebPushStatus: vi.fn(),
  subscribeCurrentDeviceToWebPush: vi.fn(),
  unsubscribeCurrentDeviceFromWebPush: vi.fn(),
}));

const apiMock = vi.hoisted(() => ({
  getWebPushDiagnostics: vi.fn(),
  revokeUserPushSubscription: vi.fn(),
}));

const toastMock = vi.hoisted(() => ({
  error: vi.fn(),
  success: vi.fn(),
}));

vi.mock('../../services/webPushNotifications', () => ({
  getWebPushStatus: webPushMock.getWebPushStatus,
  subscribeCurrentDeviceToWebPush: webPushMock.subscribeCurrentDeviceToWebPush,
  unsubscribeCurrentDeviceFromWebPush: webPushMock.unsubscribeCurrentDeviceFromWebPush,
}));

vi.mock('../../services/api', () => ({
  apiService: apiMock,
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => toastMock,
}));

function createStatus(overrides: Partial<WebPushStatus> = {}): WebPushStatus {
  return {
    status: 'not-subscribed',
    permission: 'default',
    message: 'Это устройство пока не получает push-уведомления.',
    subscriptions: [],
    publicKeyReady: true,
    ...overrides,
  };
}

describe('WebPushDevicePanel', () => {
  beforeEach(() => {
    vi.useRealTimers();
    Object.values(webPushMock).forEach((mock) => mock.mockReset());
    apiMock.revokeUserPushSubscription.mockReset();
    toastMock.error.mockReset();
    toastMock.success.mockReset();
    webPushMock.getWebPushStatus.mockResolvedValue(createStatus());
    webPushMock.subscribeCurrentDeviceToWebPush.mockResolvedValue({});
    webPushMock.unsubscribeCurrentDeviceFromWebPush.mockResolvedValue(undefined);
    apiMock.getWebPushDiagnostics.mockReset();
    apiMock.getWebPushDiagnostics.mockResolvedValue({
      enabled: true,
      ready: true,
      publicKeyConfigured: true,
      privateKeyConfigured: true,
      subjectConfigured: true,
      totalSubscriptionsCount: 2,
      activeSubscriptionsCount: 1,
      revokedSubscriptionsCount: 1,
      failedSubscriptionsCount: 1,
      usersWithActiveSubscriptionsCount: 1,
      lastSuccessfulDeliveryAt: '2026-06-18T08:00:00Z',
      lastFailedDeliveryAt: '2026-06-18T08:10:00Z',
      lastDeliveryError: 'Network error',
    });
    apiMock.revokeUserPushSubscription.mockResolvedValue(undefined);
  });

  it('loads push status and enables current device', async () => {
    webPushMock.getWebPushStatus
      .mockResolvedValueOnce(createStatus())
      .mockResolvedValueOnce(createStatus({
        status: 'subscribed',
        message: 'Push-уведомления включены на этом устройстве.',
      }));

    render(<WebPushDevicePanel />);

    expect(await screen.findByText('Это устройство пока не получает push-уведомления.')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Включить на этом устройстве' }));

    await waitFor(() => expect(webPushMock.subscribeCurrentDeviceToWebPush).toHaveBeenCalledTimes(1));
    expect(webPushMock.getWebPushStatus).toHaveBeenCalledTimes(2);
    expect(toastMock.success).toHaveBeenCalledWith('Push-уведомления включены на этом устройстве.');
  });

  it('shows realtime and web push diagnostics without blocking device status', async () => {
    render(<WebPushDevicePanel />);

    expect(await screen.findByText('Web Push: Network error')).toBeTruthy();
    expect(screen.getByText('1 активных из 2')).toBeTruthy();
    expect(screen.getByText(/ещё не проверено/)).toBeTruthy();
  });

  it('unlocks the check button when device status check times out', async () => {
    vi.useFakeTimers();
    webPushMock.getWebPushStatus.mockReturnValueOnce(new Promise(() => {}));

    render(<WebPushDevicePanel />);

    const checkButton = screen.getByRole('button', { name: 'Проверить' });
    expect((checkButton as HTMLButtonElement).disabled).toBe(true);

    await act(async () => {
      vi.advanceTimersByTime(12_000);
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(toastMock.error).toHaveBeenCalledWith('Проверка push-уведомлений заняла слишком много времени. Обновите страницу и попробуйте снова.');
    expect((checkButton as HTMLButtonElement).disabled).toBe(false);
  });

  it('keeps disable action available for the current device when server diagnostics require setup', async () => {
    webPushMock.getWebPushStatus.mockResolvedValueOnce(createStatus({
      status: 'subscribed',
      permission: 'granted',
      publicKeyReady: false,
      currentEndpoint: 'https://push.example/current',
      message: 'Push-уведомления включены на этом устройстве.',
      subscriptions: [
        {
          id: 10,
          endpoint: 'https://push.example/current',
          browser: 'Chrome',
          platform: 'Windows',
          deviceName: 'Chrome Windows',
          isActive: true,
          createdAt: '2026-06-22T09:00:00Z',
          updatedAt: '2026-06-22T09:00:00Z',
          lastSeenAt: '2026-06-22T09:00:00Z',
        },
      ],
    }));
    apiMock.getWebPushDiagnostics.mockResolvedValueOnce({
      enabled: true,
      ready: false,
      publicKeyConfigured: true,
      privateKeyConfigured: false,
      subjectConfigured: true,
      totalSubscriptionsCount: 1,
      activeSubscriptionsCount: 1,
      revokedSubscriptionsCount: 0,
      failedSubscriptionsCount: 0,
      usersWithActiveSubscriptionsCount: 1,
      lastSuccessfulDeliveryAt: '2026-06-22T09:00:00Z',
      lastFailedDeliveryAt: null,
      lastDeliveryError: null,
    });

    render(<WebPushDevicePanel />);

    expect(await screen.findByText('Push-уведомления включены на этом устройстве.')).toBeTruthy();
    expect(screen.getByText('требует настройки')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Отключить это устройство' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Включить на этом устройстве' })).toBeNull();
  });

  it('keeps device status visible when admin diagnostics are forbidden', async () => {
    apiMock.getWebPushDiagnostics.mockRejectedValueOnce(new Error('HTTP 403 Forbidden'));

    render(<WebPushDevicePanel />);

    expect(await screen.findByText('Это устройство пока не получает push-уведомления.')).toBeTruthy();
    expect(screen.getByText('ключ доступен')).toBeTruthy();
  });

  it('keeps enable action disabled when the CRM origin is not secure', async () => {
    webPushMock.getWebPushStatus.mockResolvedValueOnce(createStatus({
      status: 'insecure-context',
      permission: 'default',
      publicKeyReady: false,
      message: 'Push-уведомления работают только на защищённом HTTPS-адресе CRM.',
    }));

    render(<WebPushDevicePanel />);

    expect(await screen.findByText('Push-уведомления работают только на защищённом HTTPS-адресе CRM.')).toBeTruthy();

    const enableButton = screen.getByRole('button', { name: 'Включить на этом устройстве' });
    expect((enableButton as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(enableButton);
    expect(webPushMock.subscribeCurrentDeviceToWebPush).not.toHaveBeenCalled();
  });

  it('disables current device when it is already subscribed', async () => {
    webPushMock.getWebPushStatus
      .mockResolvedValueOnce(createStatus({
        status: 'subscribed',
        permission: 'granted',
        currentEndpoint: 'https://push.example/current',
        message: 'Push-уведомления включены на этом устройстве.',
        subscriptions: [
          {
            id: 10,
            endpoint: 'https://push.example/current',
            browser: 'Chrome',
            platform: 'Windows',
            deviceName: 'Chrome Windows',
            isActive: true,
            createdAt: '2026-06-17T10:00:00Z',
            updatedAt: '2026-06-17T10:00:00Z',
            lastSeenAt: '2026-06-17T10:00:00Z',
          },
        ],
      }))
      .mockResolvedValueOnce(createStatus());

    render(<WebPushDevicePanel />);

    expect(await screen.findByText('Chrome Windows')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Отключить это устройство' }));

    await waitFor(() => expect(webPushMock.unsubscribeCurrentDeviceFromWebPush).toHaveBeenCalledTimes(1));
    expect(toastMock.success).toHaveBeenCalledWith('Push-уведомления отключены на этом устройстве.');
  });

  it('revokes another active device from the devices list', async () => {
    webPushMock.getWebPushStatus.mockResolvedValue(createStatus({
      status: 'subscribed',
      currentEndpoint: 'https://push.example/current',
      subscriptions: [
        {
          id: 11,
          endpoint: 'https://push.example/other',
          browser: 'Firefox',
          platform: 'Android',
          deviceName: 'Телефон',
          isActive: true,
          createdAt: '2026-06-17T10:00:00Z',
          updatedAt: '2026-06-17T10:00:00Z',
          lastSeenAt: null,
        },
      ],
    }));

    render(<WebPushDevicePanel />);

    expect(await screen.findByText('Телефон')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Отключить' }));

    await waitFor(() => expect(apiMock.revokeUserPushSubscription).toHaveBeenCalledWith(11));
    expect(toastMock.success).toHaveBeenCalledWith('Push-устройство отключено.');
  });
});
