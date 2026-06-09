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
    expect(sidebar.className).toContain('lg:max-h-[calc(100vh-12rem)]');
    expect(sidebar.className).toContain('lg:overflow-hidden');
    expect(menuScroll.className).toContain('min-h-0');
    expect(menuScroll.className).toContain('flex-1');
    expect(menuScroll.className).toContain('overflow-y-auto');
    expect(menuScroll.className).toContain('overscroll-contain');
  });
});
