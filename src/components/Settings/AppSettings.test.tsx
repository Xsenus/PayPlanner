import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import { AppSettings } from './AppSettings';

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: { role: { id: 1, name: 'admin' } },
    isAdmin: () => true,
  }),
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

describe('AppSettings', () => {
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
});
