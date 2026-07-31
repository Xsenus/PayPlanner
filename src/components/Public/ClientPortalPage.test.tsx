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
    });
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
    expect(await screen.findByText('Дело клиента')).toBeTruthy();
    expect(screen.queryByText('Чужое дело')).toBeNull();
    expect(window.location.pathname).toBe('/portal');
  });
});
