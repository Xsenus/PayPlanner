import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiServiceMock, resetApiServiceMock } from '../../test/apiMock';
import { ClientPortalPage } from './ClientPortalPage';

vi.mock('../../services/api', async () => ({
  apiService: (await import('../../test/apiMock')).apiServiceMock,
}));

const CHALLENGE_ID = '22222222-2222-4222-8222-222222222222';

describe('ClientPortalPage', () => {
  beforeEach(() => {
    resetApiServiceMock(
      'previewClientPortalLogin',
      'verifyClientPortalLogin',
      'getClientPortalHome',
      'getClientPortalCase',
      'getClientPortalProfile',
      'createClientPortalDocumentGrant',
      'downloadClientPortalDocument',
      'createClientPortalMaxLink',
      'logoutClientPortal',
    );
    sessionStorage.clear();
    window.history.replaceState(null, '', `/portal/login/${CHALLENGE_ID}#token=portal-token`);
    apiServiceMock.previewClientPortalLogin.mockResolvedValue({
      displayName: 'Иван Иванов',
      emailHint: 'i***v@example.test',
      expiresAt: '2026-08-01T10:00:00Z',
    });
    apiServiceMock.verifyClientPortalLogin.mockResolvedValue({ expiresAt: '2026-08-01T20:00:00Z' });
    apiServiceMock.getClientPortalHome.mockResolvedValue({
      displayName: 'Иван Иванов',
      items: [{ publicId: '33333333-3333-4333-8333-333333333333', title: 'Дело клиента', caseNumber: 'A-1', caseType: '', status: 'Open', stage: '' }],
      total: 1,
      page: 1,
      pageSize: 20,
      upcomingActions: [{ casePublicId: '33333333-3333-4333-8333-333333333333', caseTitle: 'Дело клиента', title: 'Подать документы', dueAt: '2026-08-03T10:00:00Z', status: 'active' }],
    });
    apiServiceMock.getClientPortalProfile.mockResolvedValue({
      displayName: 'Иван Иванов',
      email: 'ivan@example.test',
      accountType: 'client',
      securityNotificationsEnabled: true,
      activeSessionCount: 2,
      activeCaseCount: 1,
    });
    apiServiceMock.getClientPortalCase.mockResolvedValue({
      publicId: '33333333-3333-4333-8333-333333333333', title: 'Дело клиента', caseNumber: 'A-1', caseType: '', status: 'Open', stage: '',
      deadlines: [], events: [], invoices: [], acts: [], contracts: [],
      approvals: [{ version: 2, status: 'Ожидает решения', requestedAt: '2026-07-31T11:00:00Z' }],
      documents: [{ publicId: '44444444-4444-4444-8444-444444444444', name: 'Документ.pdf', documentType: 'Документ', status: 'Готов', version: 1, sizeBytes: 1024, uploadedAt: '2026-07-31T10:00:00Z' }],
    });
    apiServiceMock.createClientPortalDocumentGrant.mockResolvedValue({ grantToken: 'grant', expiresAt: '2026-08-01T10:05:00Z' });
    apiServiceMock.downloadClientPortalDocument.mockResolvedValue(new Blob(['pdf']));
    apiServiceMock.createClientPortalMaxLink.mockResolvedValue({ linkId: 'max-link', payload: 'opaque', deepLinkUrl: 'https://max.ru/test_bot?start=opaque', expiresAt: '2026-08-01T10:15:00Z' });
  });

  it('removes token from URL, verifies OTP and opens only returned cases', async () => {
    render(<ClientPortalPage />);
    expect(await screen.findByText('Вход в клиентский кабинет')).toBeTruthy();
    expect(window.location.hash).toBe('');
    expect(apiServiceMock.previewClientPortalLogin).toHaveBeenCalledWith(CHALLENGE_ID, 'portal-token');

    fireEvent.change(screen.getByRole('textbox'), { target: { value: '123456' } });
    fireEvent.click(screen.getByRole('button', { name: 'Войти' }));

    await waitFor(() => expect(apiServiceMock.verifyClientPortalLogin)
      .toHaveBeenCalledWith(CHALLENGE_ID, 'portal-token', '123456'));
    expect((await screen.findAllByText('Дело клиента')).length).toBeGreaterThan(0);
    expect(screen.queryByText('Чужое дело')).toBeNull();
    expect(window.location.pathname).toBe('/portal');
  });

  it('shows upcoming actions, filters cases, opens profile and downloads through a grant', async () => {
    const createObjectUrl = vi.fn(() => 'blob:portal-document');
    const revokeObjectUrl = vi.fn();
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    const open = vi.spyOn(window, 'open').mockImplementation(() => null);
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createObjectUrl });
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: revokeObjectUrl });

    render(<ClientPortalPage />);
    fireEvent.change(await screen.findByRole('textbox'), { target: { value: '123456' } });
    fireEvent.click(screen.getByRole('button', { name: 'Войти' }));
    expect(await screen.findByText('Ближайшие сроки')).toBeTruthy();

    fireEvent.change(screen.getByLabelText('Поиск дел'), { target: { value: 'A-1' } });
    fireEvent.click(screen.getByRole('button', { name: 'Найти' }));
    await waitFor(() => expect(apiServiceMock.getClientPortalHome).toHaveBeenLastCalledWith(1, 20, 'A-1', ''));

    fireEvent.click(screen.getByRole('button', { name: 'Профиль' }));
    expect(await screen.findByText('ivan@example.test · клиент')).toBeTruthy();
    expect(screen.getByText('Уведомления безопасности о новых входах включены.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Выйти на всех устройствах' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Подключить MAX' }));
    await waitFor(() => expect(open).toHaveBeenCalledWith('https://max.ru/test_bot?start=opaque', '_blank', 'noopener,noreferrer'));

    fireEvent.click(screen.getAllByRole('button', { name: /Дело клиента/ }).at(-1)!);
    expect(await screen.findByText('Версия 2 · Ожидает решения')).toBeTruthy();
    fireEvent.click(await screen.findByRole('button', { name: /Документ\.pdf/ }));
    await waitFor(() => {
      expect(apiServiceMock.createClientPortalDocumentGrant).toHaveBeenCalledWith('44444444-4444-4444-8444-444444444444');
      expect(apiServiceMock.downloadClientPortalDocument).toHaveBeenCalledWith('44444444-4444-4444-8444-444444444444', 'grant');
    });
    expect(createObjectUrl).toHaveBeenCalledTimes(1);
    expect(revokeObjectUrl).toHaveBeenCalledWith('blob:portal-document');
    click.mockRestore();
    open.mockRestore();
  });

  it('logs out after completing the portal workflow', async () => {
    apiServiceMock.logoutClientPortal.mockImplementation(() => new Promise(() => undefined));

    render(<ClientPortalPage />);
    fireEvent.change(await screen.findByRole('textbox'), { target: { value: '123456' } });
    fireEvent.click(screen.getByRole('button', { name: 'Войти' }));
    expect(await screen.findByText('Ближайшие сроки')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Выйти' }));
    expect(apiServiceMock.logoutClientPortal).toHaveBeenCalledWith();
  });
});
