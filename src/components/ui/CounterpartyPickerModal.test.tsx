import { render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import type { Client, LegalEntitySummary } from '../../types';
import { CounterpartyPickerModal } from './CounterpartyPickerModal';

vi.mock('../../services/api', () => ({
  apiService: {
    getClientsV2: vi.fn(),
    getClients: vi.fn(),
    getLegalEntitiesPaged: vi.fn(),
    getLegalEntities: vi.fn(),
  },
}));

function createClient(overrides: Partial<Client> = {}): Client {
  return {
    id: 1,
    name: 'Alpha Client',
    email: 'alpha@example.test',
    phone: '100',
    company: '',
    address: '',
    notes: '',
    createdAt: '2026-05-14T00:00:00Z',
    isActive: true,
    ...overrides,
  };
}

function createLegalEntity(overrides: Partial<LegalEntitySummary> = {}): LegalEntitySummary {
  return {
    id: 11,
    shortName: 'Alpha Legal',
    inn: '6300000000',
    ...overrides,
  };
}

describe('CounterpartyPickerModal', () => {
  beforeEach(() => {
    vi.mocked(apiService.getClientsV2).mockReset();
    vi.mocked(apiService.getClients).mockReset();
    vi.mocked(apiService.getLegalEntitiesPaged).mockReset();
    vi.mocked(apiService.getLegalEntities).mockReset();
  });

  it('loads clients through the stable client list without calling the paged v2 endpoint', async () => {
    vi.mocked(apiService.getClients).mockResolvedValue([
      createClient({ id: 2, name: 'Zeta Client' }),
      createClient({ id: 1, name: 'Alpha Client' }),
      createClient({ id: 3, name: 'Inactive Client', isActive: false }),
    ]);

    render(<CounterpartyPickerModal open title="Pick client" onClose={vi.fn()} />);

    expect(await screen.findByText('Alpha Client')).toBeTruthy();
    expect(screen.getByText('Zeta Client')).toBeTruthy();
    expect(screen.queryByText('Inactive Client')).toBeNull();
    expect(screen.queryByText('Failed to fetch')).toBeNull();
    expect(apiService.getClientsV2).not.toHaveBeenCalled();
    expect(apiService.getClients).toHaveBeenCalledTimes(1);
  });

  it('loads legal entities through the stable list without calling the paged endpoint', async () => {
    vi.mocked(apiService.getLegalEntities).mockResolvedValue([
      createLegalEntity({ id: 12, shortName: 'Zeta Legal' }),
      createLegalEntity({ id: 11, shortName: 'Alpha Legal' }),
    ]);

    render(
      <CounterpartyPickerModal
        open
        title="Pick legal entity"
        initialScope="legalEntity"
        allowedScopes={['legalEntity']}
        onClose={vi.fn()}
      />,
    );

    expect(await screen.findByText('Alpha Legal')).toBeTruthy();
    expect(screen.getByText('Zeta Legal')).toBeTruthy();
    expect(screen.queryByText('Failed to fetch')).toBeNull();
    expect(apiService.getLegalEntitiesPaged).not.toHaveBeenCalled();
    expect(apiService.getLegalEntities).toHaveBeenCalledTimes(1);
  });
});
