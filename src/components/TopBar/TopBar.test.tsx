import { render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import TopBar from './TopBar';
import { apiService } from '../../services/api';

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: { id: 1, fullName: 'Тестовый Пользователь', role: { id: 1, name: 'admin' } },
    isAdmin: () => true,
  }),
}));

vi.mock('../../contexts/TabContext', () => ({
  useTabNavigation: () => ({ setActiveTab: vi.fn() }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({ confirm: vi.fn().mockResolvedValue(false) }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getUnreadUserNotificationCount: vi.fn(),
  },
}));

const apiMock = vi.mocked(apiService);

describe('TopBar', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    apiMock.getUnreadUserNotificationCount.mockResolvedValue(0);
  });

  it('reserves mobile space for the menu button and truncates a long title safely', () => {
    render(
      <TopBar
        title="Карточка дела с длинным названием"
        onSignOut={vi.fn()}
        onOpenProfile={vi.fn()}
        onOpenNotifications={vi.fn()}
        onOpenAppVersion={vi.fn()}
      />,
    );

    const title = screen.getByText('Карточка дела с длинным названием');
    expect(title.className).toContain('min-w-0');
    expect(title.className).toContain('truncate');
    expect(title.parentElement?.className).toContain('pl-16');
    expect(title.parentElement?.className).toContain('lg:px-6');
    expect(screen.getByRole('button', { name: 'Уведомления' }).parentElement?.className).toContain('shrink-0');
  });
});
