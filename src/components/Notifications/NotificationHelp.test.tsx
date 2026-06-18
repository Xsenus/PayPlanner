import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { AdminWebPushGuide, UserPushHelp } from './NotificationHelp';

describe('NotificationHelp', () => {
  it('explains push behavior for a regular user', () => {
    render(<UserPushHelp />);

    expect(screen.getByLabelText('Подсказка по Push-уведомлениям')).toBeTruthy();
    expect(screen.getByText('Как работают Push-уведомления')).toBeTruthy();
    expect(screen.getByText(/Включайте Push на каждом устройстве/)).toBeTruthy();
    expect(screen.getByText(/браузер запретил уведомления/)).toBeTruthy();
  });

  it('shows an administrator checklist for web push diagnostics', () => {
    render(<AdminWebPushGuide />);

    expect(screen.getByLabelText('Инструкция администратора по Web Push')).toBeTruthy();
    expect(screen.getByText('Короткая проверка для администратора')).toBeTruthy();
    expect(screen.getByText(/public key, private key и subject/)).toBeTruthy();
    expect(screen.getByText(/WebSocket-настройки nginx для SignalR/)).toBeTruthy();
  });
});
