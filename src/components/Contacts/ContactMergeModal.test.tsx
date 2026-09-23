import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService, type ContactDirectoryItemDto } from '../../services/api';
import { ContactMergeModal } from './ContactMergeModal';

const confirm = vi.hoisted(() => vi.fn().mockResolvedValue(true));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm }) }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => ({ success: vi.fn(), error: vi.fn() }) }));
vi.mock('../../services/api', () => ({
  apiService: {
    getContacts: vi.fn(),
    previewContactMerge: vi.fn(),
    mergeContacts: vi.fn(),
  },
}));

function contact(id: number, sourceType: ContactDirectoryItemDto['sourceType']): ContactDirectoryItemDto {
  return {
    sourceType, sourceId: id, category: 'people', contactType: 'Person', roles: ['Client'],
    fullName: 'Манаева Мария Михайловна', phone: '89198106343', phones: ['89198106343'], emails: [],
    isActive: true, syncToPhonebook: true, hasFullProfile: sourceType !== 'external',
  };
}

describe('contact merge dialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    confirm.mockResolvedValue(true);
  });

  it('requires a preview and explicit choice of the primary client before archiving the duplicate', async () => {
    vi.mocked(apiService.getContacts).mockResolvedValue([contact(1, 'client'), contact(2, 'client')]);
    vi.mocked(apiService.previewContactMerge).mockResolvedValue({
      canMerge: true, conflicts: [], affectedLinks: 3,
      primaryName: 'Манаева Мария Михайловна', duplicateName: 'Манаева Мария Михайловна',
      phones: ['79198106343'], emails: [],
    });
    vi.mocked(apiService.mergeContacts).mockResolvedValue({
      canMerge: true, conflicts: [], affectedLinks: 3,
      primaryName: 'Манаева Мария Михайловна', duplicateName: 'Манаева Мария Михайловна',
      phones: ['79198106343'], emails: [],
    });
    const onMerged = vi.fn();
    const onClose = vi.fn();
    render(<ContactMergeModal onMerged={onMerged} onClose={onClose} />);

    fireEvent.click(await screen.findByRole('button', { name: /2 записи/i }));
    await waitFor(() => expect(apiService.previewContactMerge).toHaveBeenCalledWith({
      primarySourceType: 'client', primarySourceId: 1,
      duplicateSourceType: 'client', duplicateSourceId: 2,
    }));
    expect(await screen.findByText(/Будет перенесено связей: 3/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Объединить и архивировать' }));

    await waitFor(() => expect(apiService.mergeContacts).toHaveBeenCalledTimes(1));
    expect(confirm).toHaveBeenCalledTimes(1);
    expect(onMerged).toHaveBeenCalledTimes(1);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('previews merging a quick duplicate into a full primary card', async () => {
    vi.mocked(apiService.getContacts).mockResolvedValue([contact(1, 'client'), contact(2, 'external')]);
    vi.mocked(apiService.previewContactMerge).mockResolvedValue({
      canMerge: true, conflicts: [], affectedLinks: 2,
      primaryName: 'Манаева Мария Михайловна', duplicateName: 'Манаева Мария Михайловна',
      phones: ['89198106343'], emails: [],
    });
    render(<ContactMergeModal onMerged={vi.fn()} onClose={vi.fn()} />);

    fireEvent.click(await screen.findByRole('button', { name: /2 записи/i }));

    await waitFor(() => expect(apiService.previewContactMerge).toHaveBeenCalledWith({
      primarySourceType: 'client', primarySourceId: 1,
      duplicateSourceType: 'external', duplicateSourceId: 2,
    }));
    expect((screen.getByRole('button', { name: 'Объединить и архивировать' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('suggests the full profile as primary when a quick contact comes first in the duplicate group', async () => {
    vi.mocked(apiService.getContacts).mockResolvedValue([contact(1, 'external'), contact(2, 'client')]);
    vi.mocked(apiService.previewContactMerge).mockResolvedValue({
      canMerge: true, conflicts: [], affectedLinks: 1,
      primaryName: 'Манаева Мария Михайловна', duplicateName: 'Манаева Мария Михайловна',
      phones: ['79198106343'], emails: [],
    });
    render(<ContactMergeModal onMerged={vi.fn()} onClose={vi.fn()} />);

    fireEvent.click(await screen.findByRole('button', { name: /2 записи/i }));

    await waitFor(() => expect(apiService.previewContactMerge).toHaveBeenCalledWith({
      primarySourceType: 'client', primarySourceId: 2,
      duplicateSourceType: 'external', duplicateSourceId: 1,
    }));
    expect((screen.getByRole('button', { name: 'Объединить и архивировать' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('still blocks archiving a full profile when the user selects a quick contact as primary', async () => {
    vi.mocked(apiService.getContacts).mockResolvedValue([contact(1, 'external'), contact(2, 'client')]);
    render(<ContactMergeModal onMerged={vi.fn()} onClose={vi.fn()} />);

    await screen.findByRole('button', { name: /2 записи/i });
    fireEvent.change(screen.getByRole('combobox', { name: 'Основная карточка' }), { target: { value: 'external:1' } });
    fireEvent.change(screen.getByRole('combobox', { name: 'Дубль в архив' }), { target: { value: 'client:2' } });

    expect(await screen.findByText(/выберите полную карточку основной/)).toBeTruthy();
    expect(apiService.previewContactMerge).not.toHaveBeenCalled();
    expect((screen.getByRole('button', { name: 'Объединить и архивировать' }) as HTMLButtonElement).disabled).toBe(true);
  });
});
