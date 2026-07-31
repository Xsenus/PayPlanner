import { render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiServiceMock, resetApiServiceMock } from '../../test/apiMock';
import type { ClientCase } from '../../types';
import { ClientPortalAccessPanel } from './ClientPortalAccessPanel';

vi.mock('../../services/api', async () => ({
  apiService: (await import('../../test/apiMock')).apiServiceMock,
}));

describe('ClientPortalAccessPanel', () => {
  beforeEach(() => {
    resetApiServiceMock('getFeatureFlags', 'getClientPortalCaseAccess', 'getClientPortalAudit');
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
  });
});
