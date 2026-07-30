import { render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { defaultRestrictedRolePermissions, defaultRolePermissions, type RolePermissions } from '../../types/permissions';
import { AppSettings } from './AppSettings';

const authMockState = vi.hoisted(() => ({
  isAdmin: true,
}));

const permissionsMockState = vi.hoisted(() => ({
  permissions: undefined as unknown,
}));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: {
      role: authMockState.isAdmin ? { id: 1, name: 'admin' } : { id: 2, name: 'Юрист' },
    },
    isAdmin: () => authMockState.isAdmin,
  }),
}));

vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: () => permissionsMockState.permissions,
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({
    confirm: vi.fn(),
  }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({
    success: vi.fn(),
    error: vi.fn(),
  }),
}));

vi.mock('../../hooks/useUserTimeZone', () => ({
  useUserTimeZone: () => 'Asia/Novosibirsk',
}));

vi.mock('./MainCompanySettings', () => ({
  MainCompanySettings: () => <div>Компании</div>,
}));

vi.mock('./MoneyAccountSettings', () => ({
  MoneyAccountSettings: () => <div>Счета денег</div>,
}));

vi.mock('./BankDirectorySettings', () => ({
  BankDirectorySettings: () => <div>Банки</div>,
}));

vi.mock('./NotificationSettings', () => ({
  NotificationSettings: () => <div>Настройки уведомлений mock</div>,
}));

vi.mock('./DatabaseMaintenanceSettings', () => ({
  DatabaseMaintenanceSettings: () => <div>Обслуживание базы данных mock</div>,
}));

vi.mock('../YandexDisk/YandexDisk', () => ({
  YandexDisk: () => <div>Я.Диск mock</div>,
}));

vi.mock('../S3Storage/S3Storage', () => ({
  S3Storage: () => <div>S3 mock</div>,
}));

function buildPermissions(overrides: Partial<RolePermissions> = {}): RolePermissions {
  return {
    ...defaultRestrictedRolePermissions,
    ...overrides,
    calendar: { ...defaultRestrictedRolePermissions.calendar, ...overrides.calendar },
    tasks: { ...defaultRestrictedRolePermissions.tasks, ...overrides.tasks },
    notes: { ...defaultRestrictedRolePermissions.notes, ...overrides.notes },
    reports: { ...defaultRestrictedRolePermissions.reports, ...overrides.reports },
    calculator: { ...defaultRestrictedRolePermissions.calculator, ...overrides.calculator },
    legalEntities: { ...defaultRestrictedRolePermissions.legalEntities, ...overrides.legalEntities },
    clients: { ...defaultRestrictedRolePermissions.clients, ...overrides.clients },
    cases: { ...defaultRestrictedRolePermissions.cases, ...overrides.cases },
    accounts: { ...defaultRestrictedRolePermissions.accounts, ...overrides.accounts },
    acts: { ...defaultRestrictedRolePermissions.acts, ...overrides.acts },
    payments: { ...defaultRestrictedRolePermissions.payments, ...overrides.payments },
    contracts: { ...defaultRestrictedRolePermissions.contracts, ...overrides.contracts },
    dictionaries: { ...defaultRestrictedRolePermissions.dictionaries, ...overrides.dictionaries },
    notifications: { ...defaultRestrictedRolePermissions.notifications, ...overrides.notifications },
    roles: { ...defaultRestrictedRolePermissions.roles, ...overrides.roles },
    audit: { ...defaultRestrictedRolePermissions.audit, ...overrides.audit },
    integrations: { ...defaultRestrictedRolePermissions.integrations, ...overrides.integrations },
    yandexDisk: { ...defaultRestrictedRolePermissions.yandexDisk, ...overrides.yandexDisk },
    s3Storage: { ...defaultRestrictedRolePermissions.s3Storage, ...overrides.s3Storage },
  };
}

describe('AppSettings', () => {
  beforeEach(() => {
    authMockState.isAdmin = true;
    permissionsMockState.permissions = defaultRolePermissions;

    vi.stubGlobal('fetch', vi.fn<typeof fetch>(async (input) => {
      if (String(input).includes('/integrations/dadata/settings')) {
        return new Response(JSON.stringify({
          apiKeyConfigured: true,
          secretConfigured: true,
          apiKeyMasked: '••••6314',
          secretMasked: '••••2906',
          canEdit: true,
          updatedAt: '2026-06-24T08:30:00Z',
        }), {
          headers: { 'content-type': 'application/json' },
        });
      }

      return new Response('{}', {
        headers: { 'content-type': 'application/json' },
      });
    }));
  });

  it('keeps the settings menu in its own scroll area', () => {
    render(<AppSettings initialSection="companies" />);

    const sidebar = screen.getByTestId('settings-sidebar');
    const menuScroll = screen.getByTestId('settings-sidebar-scroll');

    expect(sidebar.className).toContain('flex-col');
    expect(sidebar.className).toContain('p-3');
    expect(sidebar.className).toContain('lg:max-h-[calc(100vh-8rem)]');
    expect(sidebar.className).toContain('lg:overflow-hidden');
    expect(menuScroll.className).toContain('min-h-0');
    expect(menuScroll.className).toContain('flex-1');
    expect(menuScroll.className).toContain('space-y-2');
    expect(menuScroll.className).toContain('overflow-y-auto');
    expect(menuScroll.className).toContain('overscroll-contain');
  });

  it('uses compact spacing for the settings workspace', () => {
    const { container } = render(<AppSettings initialSection="companies" />);

    expect(container.firstElementChild?.className).toContain('p-3');
    expect(container.firstElementChild?.className).toContain('lg:p-4');
    expect(screen.getByTestId('settings-content').className).toContain('space-y-3');
  });

  it('shows masked DaData credentials in password inputs', async () => {
    render(<AppSettings initialSection="integrations" />);

    const tokenInput = await screen.findByPlaceholderText('Сохранён: ••••6314') as HTMLInputElement;
    const secretInput = await screen.findByPlaceholderText('Сохранён: ••••2906') as HTMLInputElement;

    expect(tokenInput.type).toBe('password');
    expect(secretInput.type).toBe('password');
    expect(tokenInput.value).toBe('');
    expect(secretInput.value).toBe('');
    expect(screen.queryByDisplayValue(/60b3fe73/i)).toBeNull();
    expect(screen.queryByDisplayValue(/56fe56f/i)).toBeNull();
  });

  it('shows only permitted settings sections for a restricted notification role', async () => {
    authMockState.isAdmin = false;
    permissionsMockState.permissions = buildPermissions({
      notifications: {
        ...defaultRestrictedRolePermissions.notifications,
        canView: true,
        canEdit: true,
      },
    });

    render(<AppSettings initialSection="yandex-disk" />);

    expect(await screen.findByText('Настройки уведомлений mock')).toBeTruthy();
    expect(screen.getByRole('button', { name: /Уведомления/i })).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Журнал задач/i })).toBeNull();
    expect(screen.queryByRole('button', { name: /Ключи интеграций/i })).toBeNull();
    expect(screen.queryByRole('button', { name: /Я\.Диск/i })).toBeNull();
    expect(screen.queryByRole('button', { name: /S3-хранилища/i })).toBeNull();
    expect(screen.queryByRole('button', { name: /Обслуживание базы данных/i })).toBeNull();
    expect(screen.queryByText('Я.Диск mock')).toBeNull();
    expect(screen.queryByText('S3 mock')).toBeNull();
  });

  it('shows only database maintenance settings for a restricted audit role', async () => {
    authMockState.isAdmin = false;
    permissionsMockState.permissions = buildPermissions({
      audit: {
        ...defaultRestrictedRolePermissions.audit,
        canView: true,
      },
    });

    render(<AppSettings initialSection="integrations" />);

    expect(await screen.findByText('Обслуживание базы данных mock')).toBeTruthy();
    expect(screen.getByRole('button', { name: /Обслуживание/i })).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Ключи интеграций/i })).toBeNull();
    expect(screen.queryByRole('button', { name: /Уведомления/i })).toBeNull();
    expect(screen.queryByRole('button', { name: /Я\.Диск/i })).toBeNull();
    expect(screen.queryByRole('button', { name: /S3-хранилища/i })).toBeNull();
    expect(screen.queryByRole('button', { name: /Версия и обновления/i })).toBeNull();
    expect(screen.queryByText('Настройки уведомлений mock')).toBeNull();
    expect(screen.queryByText('Я.Диск mock')).toBeNull();
    expect(screen.queryByText('S3 mock')).toBeNull();
  });
});
