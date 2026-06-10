import { render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import DictionariesModal from './DictionariesModal';

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

const dictionaryItem = (id: number, name: string) => ({
  id,
  name,
  description: '',
  colorHex: '#2563EB',
  isActive: true,
  createdAt: '2026-06-09T00:00:00+07:00',
});

describe('DictionariesModal accessibility', () => {
  beforeEach(() => {
    apiMocks.getDict.mockReset();
    apiMocks.getIncomeTypes.mockReset();
    apiMocks.getDict.mockResolvedValue([]);
    apiMocks.getIncomeTypes.mockResolvedValue([dictionaryItem(1, 'Доход от услуг')]);
  });

  it('names modal close and icon-only row actions', async () => {
    render(<DictionariesModal open={true} onClose={vi.fn()} onlyTab="income-income" />);

    await screen.findByText('Доход от услуг');

    expect(screen.getByRole('button', { name: 'Закрыть окно справочников' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Редактировать Доход от услуг' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Удалить Доход от услуг' })).toBeTruthy();
  });
});
