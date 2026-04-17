import { render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ActDetailPage } from './ActDetailPage';

const {
  getActMock,
  getClientsMock,
  getActResponsiblesMock,
  downloadActDocumentMock,
} = vi.hoisted(() => ({
  getActMock: vi.fn(),
  getClientsMock: vi.fn(),
  getActResponsiblesMock: vi.fn(),
  downloadActDocumentMock: vi.fn(),
}));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: { id: 1, role: { id: 1 } },
  }),
}));

vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: () => ({
    acts: {
      canEdit: true,
      canDelete: true,
    },
  }),
}));

vi.mock('../../hooks/useTranslation', () => ({
  useTranslation: () => ({ t: () => undefined }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({
    confirm: vi.fn().mockResolvedValue(true),
  }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({
    success: vi.fn(),
    error: vi.fn(),
  }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getAct: getActMock,
    getClients: getClientsMock,
    getActResponsibles: getActResponsiblesMock,
    updateAct: vi.fn(),
    deleteAct: vi.fn(),
    downloadActDocument: downloadActDocumentMock,
  },
}));

vi.mock('./ActModal', () => ({
  ActModal: () => null,
}));

vi.mock('../Clients/ClientStatusBadge', () => ({
  ClientStatusBadge: () => null,
}));

vi.mock('../ui/ObjectAuditModal', () => ({
  ObjectAuditLauncher: () => null,
}));

describe('ActDetailPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    downloadActDocumentMock.mockResolvedValue(new Blob(['<html><body><div>Preview</div></body></html>'], { type: 'text/html' }));
  });

  it('renders act preview in page layout and keeps old act data stable', async () => {
    getActMock.mockResolvedValue({
      id: 30,
      number: '30',
      date: '2026-04-08',
      amount: 18_000,
      status: 'Created',
      type: 'Income',
      clientId: 7,
      lineItems: [
        null,
        {
          id: 1,
          sourceKey: 'line-1',
          name: 'Юридическая услуга',
          quantity: '1',
          unit: 'усл.ед.',
          price: '18000',
        },
      ],
    });
    getClientsMock.mockResolvedValue({
      items: [
        {
          id: 7,
          name: 'Тестовый клиент',
          email: '',
          phone: '',
          company: '',
          address: '',
          notes: '',
          createdAt: '2026-04-01T00:00:00Z',
          isActive: true,
        },
      ],
    });
    getActResponsiblesMock.mockResolvedValue({ items: [] });

    render(<ActDetailPage actId={30} onBack={vi.fn()} />);

    await waitFor(() => {
      expect(screen.getByText('Печатный шаблон акта')).toBeTruthy();
    });

    expect(screen.getByText('Тестовый клиент')).toBeTruthy();
    expect(screen.getByText(/Юридическая услуга/)).toBeTruthy();
    await waitFor(() => {
      expect(downloadActDocumentMock).toHaveBeenCalledWith(30, 'html', { includeSignatureAndSeal: false });
    });
  });
});
