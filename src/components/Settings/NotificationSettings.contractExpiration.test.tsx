import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { NotificationSettings } from './NotificationSettings';

const toast = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => toast }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm: vi.fn() }) }));
vi.mock('./ContractExpirationRecipientSettings', () => ({
  ContractExpirationRecipientSettings: ({ canEdit }: { canEdit: boolean }) => <div data-testid="contract-expiration-recipients-entry" data-can-edit={String(canEdit)}>Напоминания об окончании договоров</div>,
}));
vi.mock('../../services/api', () => ({ apiService: {
  getNotificationSettings: vi.fn().mockResolvedValue(null),
  getNotificationChannels: vi.fn().mockResolvedValue([]),
  getWebPushDiagnostics: vi.fn().mockResolvedValue(null),
  getUserNotificationTypes: vi.fn().mockResolvedValue([]),
  getNotificationRolePreferences: vi.fn().mockResolvedValue({ roles: [] }),
  getNotificationDeliveries: vi.fn().mockResolvedValue({ items: [], total: 0 }),
  getMaxOutboxMessages: vi.fn().mockResolvedValue([]),
} }));
function expectVisible(element: HTMLElement) {
  expect(document.body.contains(element)).toBe(true);
  let current: HTMLElement | null = element;
  while (current) {
    expect(current.hidden).toBe(false);
    const style = window.getComputedStyle(current);
    expect(style.display).not.toBe('none');
    expect(style.visibility).not.toBe('hidden');
    expect(style.visibility).not.toBe('collapse');
    expect(Number.parseFloat(style.opacity)).not.toBe(0);
    current = current.parentElement;
  }
}

afterEach(() => { window.history.replaceState(null, '', '/'); });

describe('notification settings contract recipient entry', () => {
  it.each([true, false])('passes the existing notification edit permission as %s', async (canEdit) => {
    window.history.replaceState(null, '', '/?ppTab=settings&settingsSection=notifications');
    render(<NotificationSettings canEdit={canEdit} />);
    expect((await screen.findByTestId('contract-expiration-recipients-entry')).getAttribute('data-can-edit')).toBe(String(canEdit));
    expectVisible(screen.getByText('Каналы уведомлений'));
  });
});
