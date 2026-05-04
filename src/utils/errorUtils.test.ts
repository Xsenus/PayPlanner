import { describe, expect, it } from 'vitest';
import { extractDisplayErrorMessage } from './errorUtils';

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
});
