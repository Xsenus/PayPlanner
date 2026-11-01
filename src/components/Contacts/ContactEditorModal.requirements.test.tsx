import { act, render } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import { ContactEditorModal } from './ContactEditorModal';
import { useFormRequirements } from '../../hooks/useFormRequirements';
import type { ContactDirectoryItemDto } from '../../services/api';

vi.mock('../../hooks/useToast', () => ({ useToast: () => ({ error: vi.fn(), success: vi.fn() }) }));
vi.mock('../../hooks/useFormRequirements', () => ({ useFormRequirements: vi.fn(() => ({ isRequired: () => false })) }));
vi.mock('../ui/ObjectAuditModal', () => ({ ObjectAuditLauncher: () => null }));
it('requirements caller: contact editor scopes source identity, mode and authority and stops when closed', async () => {
  const contact: ContactDirectoryItemDto = { sourceId: 1, sourceType: 'external', category: 'external', contactType: 'Person', fullName: 'Тестовый контакт', roles: ['Contact'], hasFullProfile: false, phones: [], emails: [], isActive: true, syncToPhonebook: false };
  const p = { open: true, mode: 'edit-external' as const, contact, legalEntities: [], responsibles: [], canManageResponsible: false, ownerScope: 'all', onClose: vi.fn(), onSaved: vi.fn() };
  const view = render(<ContactEditorModal {...p} />); const first = vi.mocked(useFormRequirements).mock.lastCall; expect(first?.[0]).toBe('contacts-external'); expect(first?.[1]).toBe(true);
  view.rerender(<ContactEditorModal {...p} mode="edit-client" contact={{ ...contact, sourceType: 'client', sourceId: 2 }} ownerScope="own" />); const second = vi.mocked(useFormRequirements).mock.lastCall;
  expect(second?.[0]).toBe('contacts-client'); expect(second?.[2]?.ownerScope).not.toBe(first?.[2]?.ownerScope);
  view.rerender(<ContactEditorModal {...p} open={false} />); expect(vi.mocked(useFormRequirements).mock.lastCall?.[1]).toBe(false); await act(async () => {});
});
