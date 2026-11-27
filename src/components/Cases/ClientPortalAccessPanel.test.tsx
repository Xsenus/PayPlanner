import { fireEvent, render, screen, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiServiceMock, resetApiServiceMock } from '../../test/apiMock';
import type { ClientCase } from '../../types';
import { ClientPortalAccessPanel } from './ClientPortalAccessPanel';

vi.mock('../../services/api', async () => ({
  apiService: (await import('../../test/apiMock')).apiServiceMock,
}));

describe('ClientPortalAccessPanel', () => {
  beforeEach(() => {
    resetApiServiceMock('getFeatureFlags', 'getClientPortalCaseAccess', 'getClientPortalAudit',
      'previewClientPortalCase', 'saveClientPortalAccess', 'sendClientPortalLogin', 'createClientPortalLoginInvitation');
    apiServiceMock.getFeatureFlags.mockResolvedValue({ legal: { clientPortal: true } });
    apiServiceMock.getClientPortalAudit.mockResolvedValue([]);
    apiServiceMock.getClientPortalCaseAccess.mockResolvedValue([{
      accountId: 7,
      displayName: 'Иван Иванов',
      email: 'ivan@example.test',
      accountType: 'client',
      isActive: true,
      canViewCaseSummary: true,
      canViewDeadlines: true,
      canViewDocuments: true,
      canViewFinance: false,
      canViewApprovals: true,
      canViewMessages: false,
      documentIds: [],
    }]);
  });

  it('shows an explicit compact explanation of granted rights', async () => {
    render(<ClientPortalAccessPanel caseItem={{ id: 15 } as ClientCase} documents={[]} />);

    const rights = await screen.findByLabelText('Разрешения: Иван Иванов');
    expect(rights.textContent).toContain('Карточка дела');
    expect(rights.textContent).toContain('Сроки и события');
    expect(rights.textContent).toContain('Документы');
    expect(rights.textContent).toContain('Согласования');
    expect(rights.textContent).not.toContain('Финансы');
    expect(screen.queryByRole('button', { name: 'Посмотреть как клиент' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Сгенерировать код без письма' })).toBeNull();
  });

  it('shows a draft preview with client login disabled and does not offer document downloads', async () => {
    apiServiceMock.getFeatureFlags.mockResolvedValue({ legal: { clientPortal: false } });
    apiServiceMock.previewClientPortalCase.mockResolvedValue({
      title: 'Тестовое дело', caseNumber: '15', status: 'Открыто',
      deadlines: [], invoices: [], approvals: [],
      documents: [{ publicId: 'document-1', name: 'Готовый документ', version: 1 }],
    });
    render(<ClientPortalAccessPanel caseItem={{ id: 15 } as ClientCase} documents={[]} canPreview />);

    fireEvent.click(await screen.findByRole('button', { name: 'Посмотреть как клиент' }));
    expect(await screen.findByRole('heading', { name: 'Тестовое дело' })).toBeTruthy();
    expect(screen.getByText('Готовый документ · версия 1')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Готовый документ/ })).toBeNull();
    expect(apiServiceMock.previewClientPortalCase).toHaveBeenCalledWith(15, expect.objectContaining({
      canViewDeadlines: true,
      canViewDocuments: false,
      documentIds: [],
    }));
    expect(apiServiceMock.saveClientPortalAccess).not.toHaveBeenCalled();
    expect(apiServiceMock.sendClientPortalLogin).not.toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: 'Предоставить доступ без письма' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Закрыть предпросмотр' }));
    expect(screen.queryByRole('heading', { name: 'Тестовое дело' })).toBeNull();
  });

  it('uses the saved access of the selected recipient', async () => {
    apiServiceMock.previewClientPortalCase.mockResolvedValue({
      title: 'Дело с выданным доступом', deadlines: [], documents: [], invoices: [], approvals: [],
    });
    render(<ClientPortalAccessPanel caseItem={{ id: 15 } as ClientCase} documents={[]} canPreview />);

    await screen.findByLabelText('Разрешения: Иван Иванов');
    fireEvent.click(screen.getByRole('button', { name: 'Посмотреть как клиент' }));
    expect(await screen.findByRole('heading', { name: 'Кабинет клиента · Иван Иванов' })).toBeTruthy();
    expect(apiServiceMock.previewClientPortalCase).toHaveBeenCalledWith(15, { accountId: 7 });
  });

  it('can resend a login email after access was saved and the first email failed', async () => {
    apiServiceMock.getClientPortalCaseAccess.mockResolvedValueOnce([]);
    apiServiceMock.saveClientPortalAccess.mockResolvedValue({ id: 7 });
    apiServiceMock.sendClientPortalLogin
      .mockRejectedValueOnce(new Error('Почта недоступна'))
      .mockResolvedValueOnce({ challengeId: 'replacement', emailHint: 'i***n@example.test', expiresAt: '2026-11-26T12:00:00Z' });
    render(<ClientPortalAccessPanel caseItem={{ id: 15 } as ClientCase} documents={[]} />);

    fireEvent.change(await screen.findByPlaceholderText('ФИО получателя'), { target: { value: 'Иван Иванов' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Email для входа' }), { target: { value: 'ivan@example.test' } });
    fireEvent.click(await screen.findByRole('button', { name: 'Предоставить доступ и отправить письмо' }));
    expect(await screen.findByText(/Доступ сохранён. Отправка письма не подтверждена/)).toBeTruthy();

    fireEvent.click(await screen.findByRole('button', { name: 'Отправить новый код' }));
    expect(await screen.findByText(/Письмо для входа отправлено на i\*\*\*n@example\.test/)).toBeTruthy();
    expect(apiServiceMock.saveClientPortalAccess).toHaveBeenCalledTimes(1);
    expect(apiServiceMock.sendClientPortalLogin.mock.calls).toEqual([[7, 15], [7, 15]]);
  });

  it('generates a code for the selected account and clears the details after hiding them', async () => {
    const loginUrl = 'https://crm.example.test/portal/login/test#token=example';
    apiServiceMock.createClientPortalLoginInvitation.mockResolvedValue({
      loginUrl, code: '012345', expiresAt: new Date(Date.now() + 60_000).toISOString(),
    });
    render(<ClientPortalAccessPanel caseItem={{ id: 15 } as ClientCase} documents={[]} canPreview />);

    fireEvent.click(await screen.findByRole('button', { name: 'Сгенерировать код без письма' }));
    const invitation = await screen.findByRole('region', { name: 'Приглашение для входа' });
    expect(within(invitation).getByRole('heading', { name: 'Код для ivan@example.test' })).toBeTruthy();
    expect(within(invitation).getByDisplayValue(loginUrl)).toBeTruthy();
    expect(within(invitation).getByDisplayValue('012345')).toBeTruthy();
    expect(apiServiceMock.createClientPortalLoginInvitation).toHaveBeenCalledExactlyOnceWith(15, 7);
    expect(apiServiceMock.sendClientPortalLogin).not.toHaveBeenCalled();
    expect(apiServiceMock.saveClientPortalAccess).not.toHaveBeenCalled();

    fireEvent.click(within(invitation).getByRole('button', { name: 'Скрыть приглашение' }));
    expect(screen.queryByDisplayValue('012345')).toBeNull();
    expect(screen.queryByDisplayValue(loginUrl)).toBeNull();
  });

  it('can grant first access without sending email', async () => {
    apiServiceMock.getClientPortalCaseAccess.mockResolvedValueOnce([]);
    apiServiceMock.saveClientPortalAccess.mockResolvedValue({ id: 7 });
    apiServiceMock.createClientPortalLoginInvitation.mockResolvedValue({
      loginUrl: 'https://crm.example.test/portal/login/test#token=example',
      code: '654321', expiresAt: new Date(Date.now() + 60_000).toISOString(),
    });
    render(<ClientPortalAccessPanel caseItem={{ id: 15 } as ClientCase} documents={[]} canPreview />);

    fireEvent.change(await screen.findByPlaceholderText('ФИО получателя'), { target: { value: 'Иван Иванов' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Email для входа' }), { target: { value: 'ivan@example.test' } });
    fireEvent.click(screen.getByRole('button', { name: 'Предоставить доступ без письма' }));

    expect(await screen.findByDisplayValue('654321')).toBeTruthy();
    expect(apiServiceMock.saveClientPortalAccess).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({
      clientCaseId: 15, email: 'ivan@example.test',
    }));
    expect(apiServiceMock.createClientPortalLoginInvitation).toHaveBeenCalledExactlyOnceWith(15, 7);
    expect(apiServiceMock.sendClientPortalLogin).not.toHaveBeenCalled();
  });
});
