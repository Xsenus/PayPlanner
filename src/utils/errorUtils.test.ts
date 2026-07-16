import { describe, expect, it } from 'vitest';
import { extractDisplayErrorMessage, isHttpErrorStatus } from './errorUtils';

describe('extractDisplayErrorMessage', () => {
  it('adds mapped missing field labels from validation payload', () => {
    const error = new Error(
      'HTTP 400 - {"message":"Не заполнены обязательные поля","missingFields":["email","phone"]}',
    );

    const message = extractDisplayErrorMessage(error, 'Что-то пошло не так', {
      email: 'Email',
      phone: 'Телефон',
    });

    expect(message).toBe('Не заполнены обязательные поля: Email, Телефон');
  });

  it('falls back when the backend returned only a bare HTTP envelope', () => {
    const error = new Error('HTTP 500 at /api/profile');

    const message = extractDisplayErrorMessage(error, 'Не удалось сохранить профиль');

    expect(message).toBe('Не удалось сохранить профиль');
  });

  it('shows an explicit message when the session expired', () => {
    const error = new Error('HTTP 401 Unauthorized at /api/tasks');

    const message = extractDisplayErrorMessage(error, 'Не удалось сохранить задачу.');

    expect(message).toBe('Сессия истекла. Войдите снова.');
  });

  it('shows an explicit message when the action is forbidden', () => {
    const error = new Error('HTTP 403 Forbidden at /api/tasks');

    const message = extractDisplayErrorMessage(error, 'Не удалось сохранить задачу.');

    expect(message).toBe('Недостаточно прав для выполнения действия.');
  });

  it('falls back for browser-level network errors', () => {
    const error = new Error('Failed to fetch');

    const message = extractDisplayErrorMessage(error, 'Не удалось загрузить данные.');

    expect(message).toBe('Не удалось загрузить данные.');
  });

  it('returns the stripped plain-text payload when it is not JSON', () => {
    const error = new Error('HTTP 409 - Клиент с таким email уже существует');

    const message = extractDisplayErrorMessage(error, 'Не удалось создать клиента');

    expect(message).toBe('Клиент с таким email уже существует');
  });

  it('recognizes an HTTP status in API errors', () => {
    expect(isHttpErrorStatus(new Error('HTTP 404 Not Found at /api/tasks/15'), 404)).toBe(true);
    expect(isHttpErrorStatus(new Error('HTTP 403 Forbidden at /api/tasks/15'), 404)).toBe(false);
    expect(isHttpErrorStatus(new Error('Failed to fetch'), 404)).toBe(false);
  });
});
