import { render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ActDetailPage } from './ActDetailPage';

const {
  getActMock,
  getClientsMock,
  getActResponsiblesMock,
} = vi.hoisted(() => ({
  getActMock: vi.fn(),
  getClientsMock: vi.fn(),
  getActResponsiblesMock: vi.fn(),
}));

vi.mock('../../hooks/useTranslation', () => ({
  useTranslation: () => ({ t: () => undefined }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getAct: getActMock,
    getClients: getClientsMock,
    getActResponsibles: getActResponsiblesMock,
    updateAct: vi.fn(),
    downloadActDocument: vi.fn(),
  },
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
  });

  it('renders old act data without crashing when arrays come in a non-ideal shape', async () => {
    getActMock.mockResolvedValue({
      id: 30,
      number: '30',
      date: '2026-04-08',
      amount: 18_000,
      status: 'Created',
      clientId: 7,
      lineItems: [
        null,
        {
          id: 1,
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
      expect(screen.getByText('Редактировать акт')).toBeTruthy();
    });

    expect(screen.getByText('Юридическая услуга')).toBeTruthy();
    expect(screen.getByText('Тестовый клиент')).toBeTruthy();
  });
});
