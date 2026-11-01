import { act, render } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ClientModal } from './ClientModal';
import { useFormRequirements } from '../../hooks/useFormRequirements';
import { CLIENT_PROFILE_NOTES_PREFIX } from './clientQuickModalUtils';
import type { Client } from '../../types';

vi.mock('../../hooks/useTranslation', () => ({ useTranslation: () => ({ t: (key: string) => key }) }));
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm: vi.fn() }) }));
vi.mock('../../hooks/useFormRequirements', () => ({ useFormRequirements: vi.fn(() => ({ isRequired: () => false })) }));
describe('quick client requirements', () => {
  it.each(['individual', 'legal'] as const)('requirements caller: %s quick client selects its correct section from the first render and scopes the client', async kind => {
    const client: Client = { id: 1, name: 'Тестовый клиент', phone: '', email: '', company: '', address: '', notes: `${CLIENT_PROFILE_NOTES_PREFIX}${JSON.stringify({ kind })}`, isActive: true, createdAt: '2026-11-01T00:00:00Z' };
    const p = { isOpen: true, client, ownerScope: 'all', onClose: vi.fn(), onSubmit: vi.fn() }; vi.mocked(useFormRequirements).mockClear(); const view = render(<ClientModal {...p} />);
    const first = vi.mocked(useFormRequirements).mock.calls[0]; expect(first?.[0]).toBe(kind === 'legal' ? 'clients-modal-legal' : 'clients-modal'); expect(first?.[1]).toBe(true);
    view.rerender(<ClientModal {...p} client={{ ...client, id: 2 }} ownerScope="own" />); expect(vi.mocked(useFormRequirements).mock.lastCall?.[2]?.ownerScope).not.toBe(first?.[2]?.ownerScope);
    view.rerender(<ClientModal {...p} isOpen={false} />); expect(vi.mocked(useFormRequirements).mock.lastCall?.[1]).toBe(false); await act(async () => {});
  });
});
