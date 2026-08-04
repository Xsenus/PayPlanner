import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { Login } from './Login';
import { Register } from './Register';
import { authService } from '../../services/authService';

const { signInMock } = vi.hoisted(() => ({ signInMock: vi.fn() }));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({ signIn: signInMock }),
}));

vi.mock('../../services/authService', () => ({
  authService: { register: vi.fn() },
}));

vi.mock('./ExternalAuthPanel', () => ({
  ExternalAuthPanel: () => null,
}));

describe('Формы внутренней авторизации', () => {
  beforeEach(() => {
    signInMock.mockReset().mockResolvedValue(undefined);
    vi.mocked(authService.register).mockReset().mockResolvedValue(undefined);
  });

  it('не отправляет регистрацию с паролем короче восьми символов', async () => {
    const user = userEvent.setup();
    render(<Register />);

    await user.type(screen.getByPlaceholderText('Иван Иванов'), 'Иван Иванов');
    await user.type(screen.getByPlaceholderText('you@example.com'), 'user@example.com');
    await user.type(screen.getByPlaceholderText('Введите пароль'), '1234567');
    await user.type(screen.getByPlaceholderText('Повторите пароль'), '1234567');
    await user.click(screen.getByRole('button', { name: 'Создать аккаунт' }));

    expect(screen.getByText('Пароль должен содержать не менее 8 символов.')).toBeTruthy();
    expect(authService.register).not.toHaveBeenCalled();
  });

  it('ограничивает длину полей теми же пределами, что и API', () => {
    const { unmount } = render(<Register />);

    expect(screen.getByPlaceholderText('Иван Иванов').getAttribute('minlength')).toBe('2');
    expect(screen.getByPlaceholderText('Иван Иванов').getAttribute('maxlength')).toBe('200');
    expect(screen.getByPlaceholderText('you@example.com').getAttribute('maxlength')).toBe('320');
    expect(screen.getByPlaceholderText('Введите пароль').getAttribute('minlength')).toBe('8');
    expect(screen.getByPlaceholderText('Введите пароль').getAttribute('maxlength')).toBe('128');
    unmount();

    render(<Login />);
    expect(screen.getByPlaceholderText('you@example.com').getAttribute('maxlength')).toBe('320');
    expect(screen.getByPlaceholderText('Введите пароль').getAttribute('maxlength')).toBe('512');
  });
});
