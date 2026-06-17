import { describe, expect, it, vi } from 'vitest';

import { getWebPushStatus, urlBase64ToUint8Array } from './webPushNotifications';

const apiMock = vi.hoisted(() => ({
  getWebPushPublicKey: vi.fn(),
  getUserPushSubscriptions: vi.fn(),
  upsertUserPushSubscription: vi.fn(),
  revokeUserPushSubscriptionByEndpoint: vi.fn(),
}));

vi.mock('./api', () => ({
  apiService: apiMock,
}));

describe('webPushNotifications', () => {
  it('decodes VAPID public key from URL-safe base64', () => {
    const decoded = urlBase64ToUint8Array('SGVsbG8td29ybGQ');

    expect(Array.from(decoded)).toEqual(Array.from(new TextEncoder().encode('Hello-world')));
  });

  it('returns unsupported status when browser APIs are unavailable', async () => {
    const originalServiceWorker = navigator.serviceWorker;
    const originalPushManager = window.PushManager;
    const originalNotification = window.Notification;

    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: undefined,
    });
    Object.defineProperty(window, 'PushManager', {
      configurable: true,
      value: undefined,
    });
    Object.defineProperty(window, 'Notification', {
      configurable: true,
      value: undefined,
    });

    await expect(getWebPushStatus()).resolves.toMatchObject({
      status: 'unsupported',
      permission: 'unsupported',
      subscriptions: [],
      publicKeyReady: false,
    });

    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: originalServiceWorker,
    });
    Object.defineProperty(window, 'PushManager', {
      configurable: true,
      value: originalPushManager,
    });
    Object.defineProperty(window, 'Notification', {
      configurable: true,
      value: originalNotification,
    });
  });
});
