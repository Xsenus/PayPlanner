import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { runInNewContext } from 'node:vm';
import { describe, expect, it, vi } from 'vitest';

type WorkerEventListener = (event: unknown) => void;

function loadWorker() {
  const listeners = new Map<string, WorkerEventListener>();
  const registration = {
    showNotification: vi.fn().mockResolvedValue(undefined),
  };
  const clients = {
    matchAll: vi.fn(),
    openWindow: vi.fn().mockResolvedValue(undefined),
  };
  const self = {
    addEventListener: vi.fn((eventName: string, listener: WorkerEventListener) => {
      listeners.set(eventName, listener);
    }),
    registration,
    clients,
    location: {
      origin: 'https://crm.example.com',
    },
  };
  const script = readFileSync(resolve(process.cwd(), 'public/payplanner-push-worker.js'), 'utf8');

  runInNewContext(script, { self, URL });

  return { listeners, registration, clients };
}

async function dispatchNotificationClick(
  listener: WorkerEventListener,
  data: Record<string, unknown> | undefined,
) {
  const waitUntilPromises: Promise<unknown>[] = [];
  const event = {
    notification: {
      data,
      close: vi.fn(),
    },
    waitUntil: vi.fn((promise: Promise<unknown>) => {
      waitUntilPromises.push(promise);
    }),
  };

  listener(event);
  await Promise.all(waitUntilPromises);

  return event;
}

describe('payplanner-push-worker', () => {
  it('navigates and focuses an existing CRM tab for relative notification urls', async () => {
    const { listeners, clients } = loadWorker();
    const foreignClient = {
      url: 'https://other.example.com/app',
      focus: vi.fn().mockResolvedValue(undefined),
    };
    const crmClient = {
      url: 'https://crm.example.com/notifications',
      navigate: vi.fn().mockResolvedValue(undefined),
      focus: vi.fn().mockResolvedValue(undefined),
    };
    clients.matchAll.mockResolvedValue([foreignClient, crmClient]);

    const event = await dispatchNotificationClick(
      listeners.get('notificationclick')!,
      { url: '/tasks/15?from=push' },
    );

    expect(event.notification.close).toHaveBeenCalledTimes(1);
    expect(clients.matchAll).toHaveBeenCalledWith({ type: 'window', includeUncontrolled: true });
    expect(crmClient.navigate).toHaveBeenCalledWith('https://crm.example.com/tasks/15?from=push');
    expect(crmClient.focus).toHaveBeenCalledTimes(1);
    expect(clients.openWindow).not.toHaveBeenCalled();
  });

  it('opens a new CRM tab when there is no same-origin client', async () => {
    const { listeners, clients } = loadWorker();
    clients.matchAll.mockResolvedValue([
      {
        url: 'https://mail.example.com/inbox',
        focus: vi.fn().mockResolvedValue(undefined),
      },
    ]);

    await dispatchNotificationClick(
      listeners.get('notificationclick')!,
      { url: '/payments/42' },
    );

    expect(clients.openWindow).toHaveBeenCalledWith('https://crm.example.com/payments/42');
  });

  it('falls back to notification center for missing or external notification urls', async () => {
    const { listeners, clients } = loadWorker();
    const crmClient = {
      url: 'https://crm.example.com/calendar',
      navigate: vi.fn().mockResolvedValue(undefined),
      focus: vi.fn().mockResolvedValue(undefined),
    };
    clients.matchAll.mockResolvedValue([crmClient]);

    await dispatchNotificationClick(
      listeners.get('notificationclick')!,
      { url: 'https://evil.example.com/phishing' },
    );
    await dispatchNotificationClick(
      listeners.get('notificationclick')!,
      undefined,
    );

    expect(crmClient.navigate).toHaveBeenNthCalledWith(1, 'https://crm.example.com/notifications');
    expect(crmClient.navigate).toHaveBeenNthCalledWith(2, 'https://crm.example.com/notifications');
  });
});
