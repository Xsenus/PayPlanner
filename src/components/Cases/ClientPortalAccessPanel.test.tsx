import { fireEvent, render, screen } from '@testing-library/react';
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
      'previewClientPortalCase', 'saveClientPortalAccess', 'sendClientPortalLogin');
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
});
