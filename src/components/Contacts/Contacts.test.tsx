import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { Contacts } from './Contacts';
import { apiService, type ContactDirectoryItemDto } from '../../services/api';

const isAdminMock = vi.hoisted(() => vi.fn(() => false));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({ isAdmin: isAdminMock }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({ confirm: vi.fn().mockResolvedValue(true) }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({
    success: vi.fn(),
    error: vi.fn(),
    info: vi.fn(),
    warning: vi.fn(),
  }),
}));

vi.mock('../../hooks/usePersistedTableSettings', () => ({
  usePersistedTableSettings: () => ({
    columnOrder: ['contact'],
    orderedVisibleColumns: ['contact'],
    visibleColumns: new Set(['contact']),
    pageSize: 20,
    setPageSize: vi.fn(),
    draggingColumn: null,
    toggleColumn: vi.fn(),
    moveColumn: vi.fn(),
    handleColumnDragStart: vi.fn(),
    handleColumnDragOver: vi.fn(),
    handleColumnDrop: vi.fn(),
    handleColumnDragEnd: vi.fn(),
    handleResizeStart: vi.fn(),
    getColumnWidth: () => 320,
    getTableWidth: () => 1120,
    resetTableSettings: vi.fn(),
  }),
}));

vi.mock('./ContactEditorModal', () => ({ ContactEditorModal: () => null }));
vi.mock('./YealinkImportPreviewModal', () => ({ YealinkImportPreviewModal: () => null }));

vi.mock('../../services/api', () => ({
  apiService: {
    getContacts: vi.fn(),
    getLegalEntities: vi.fn(),
    getActResponsibles: vi.fn(),
    promoteExternalContact: vi.fn(),
  },
}));

function createContact(id: number, fullName: string): ContactDirectoryItemDto {
  return {
    sourceType: 'external',
    category: 'external',
    sourceId: id,
    contactType: 'Person',
    roles: ['Contact'],
    hasFullProfile: false,
    fullName,
    phones: [],
    emails: [],
    isActive: true,
    syncToPhonebook: true,
  };
}

describe('Contacts phone directory', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiService.getLegalEntities).mockResolvedValue([]);
    vi.mocked(apiService.getActResponsibles).mockResolvedValue([]);
  });

  it('shows one directory for people and organizations', async () => {
    const person = createContact(1, 'Иван Иванов');
    const organization: ContactDirectoryItemDto = {
      ...createContact(2, 'ООО Ромашка'),
      contactType: 'Organization' as const,
      inn: '5401000000',
      roles: ['Client', 'Supplier'],
      syncToPhonebook: false,
    };
    vi.mocked(apiService.getContacts).mockResolvedValue([person, organization]);

    render(<Contacts />);

    expect(await screen.findByText('Иван Иванов')).toBeTruthy();
    expect(screen.getByText('ООО Ромашка')).toBeTruthy();
    expect(screen.getByRole('button', { name: /Люди\s*1/i })).toBeTruthy();
    expect(screen.getByRole('button', { name: /Организации\s*1/i })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Добавить контакт' })).toBeTruthy();
    expect(screen.getByText('ИНН: 5401000000')).toBeTruthy();
  });

  it('aborts both obsolete directory requests and keeps the latest response', async () => {
    const signals: AbortSignal[] = [];
    const resolvers: Array<(value: ContactDirectoryItemDto[]) => void> = [];
    vi.mocked(apiService.getContacts).mockImplementation((_params, options) => {
      if (options?.signal) signals.push(options.signal);
      return new Promise((resolve) => resolvers.push(resolve));
    });

    render(<Contacts initialScope="external" />);

    await waitFor(() => expect(apiService.getContacts).toHaveBeenCalledTimes(2));
    expect(signals[0]).toBe(signals[1]);
    expect(signals[0]?.aborted).toBe(false);

    fireEvent.change(screen.getByPlaceholderText('ФИО, телефон, email, организация'), {
      target: { value: 'текущий' },
    });

    await waitFor(() => expect(apiService.getContacts).toHaveBeenCalledTimes(4), { timeout: 1500 });
    expect(signals[0]?.aborted).toBe(true);
    expect(signals[1]?.aborted).toBe(true);
    expect(signals[2]).toBe(signals[3]);
    expect(signals[2]?.aborted).toBe(false);

    await act(async () => {
      resolvers[2]([createContact(2, 'Текущий контакт')]);
      resolvers[3]([createContact(2, 'Текущий контакт')]);
    });
    expect(await screen.findByText('Текущий контакт')).toBeTruthy();

    await act(async () => {
      resolvers[0]([createContact(1, 'Устаревший контакт')]);
      resolvers[1]([createContact(1, 'Устаревший контакт')]);
    });
    expect(screen.queryByText('Устаревший контакт')).toBeNull();
    expect(screen.getByText('Текущий контакт')).toBeTruthy();
  });

  it('aborts both directory requests when the screen unmounts', async () => {
    const signals: AbortSignal[] = [];
    vi.mocked(apiService.getContacts).mockImplementation((_params, options) => {
      if (options?.signal) signals.push(options.signal);
      return new Promise(() => undefined);
    });

    const { unmount } = render(<Contacts initialScope="external" />);

    await waitFor(() => expect(apiService.getContacts).toHaveBeenCalledTimes(2));
    expect(signals[0]).toBe(signals[1]);
    expect(signals[0]?.aborted).toBe(false);

    unmount();

    expect(signals[0]?.aborted).toBe(true);
    expect(signals[1]?.aborted).toBe(true);
  });
});
