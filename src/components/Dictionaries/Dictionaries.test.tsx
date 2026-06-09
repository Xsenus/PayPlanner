import { render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Dictionaries } from './Dictionaries';

const apiMocks = vi.hoisted(() => ({
  getDict: vi.fn(),
  getIncomeTypes: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getDict: apiMocks.getDict,
    getIncomeTypes: apiMocks.getIncomeTypes,
  },
}));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    isAdmin: () => true,
  }),
}));

const dictionaryItem = (id: number, name: string) => ({
  id,
  name,
  description: '',
  colorHex: '#2563EB',
  isActive: true,
  createdAt: '2026-06-09T00:00:00+07:00',
});

describe('Dictionaries page layout', () => {
  beforeEach(() => {
    apiMocks.getDict.mockReset();
    apiMocks.getIncomeTypes.mockReset();
    apiMocks.getDict.mockResolvedValue([]);
    apiMocks.getIncomeTypes.mockResolvedValue([dictionaryItem(1, 'Доход от услуг')]);
  });

  it('keeps dictionaries and entries in internal scroll areas', async () => {
    render(<Dictionaries />);

    await waitFor(() => expect(apiMocks.getIncomeTypes).toHaveBeenCalled());

    expect(screen.queryByText('Управляйте справочниками системы')).toBeFalsy();
    expect(screen.queryByText('Просмотр справочников системы')).toBeFalsy();

    expect(screen.getByTestId('dictionaries-layout').className).toContain('items-start');
    expect(screen.getByTestId('dictionaries-nav').className).toContain('max-h-[calc(100vh-12rem)]');
    expect(screen.getByTestId('dictionaries-nav-scroll').className).toContain('overflow-y-auto');
    expect(screen.getByTestId('dictionaries-items-scroll').className).toContain('overflow-y-auto');
  });
});
