import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import { CaseModal } from './CaseModal';
import type { ClientCase } from '../../types';

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({
    confirm: vi.fn().mockResolvedValue(true),
  }),
}));

function createCase(overrides: Partial<ClientCase> = {}): ClientCase {
  return {
    id: 1,
    clientId: 10,
    title: 'Тестовое дело',
    description: 'Описание',
    status: 'Open',
    createdAt: '2026-04-21T10:00:00Z',
    ...overrides,
  } as ClientCase;
}

describe('CaseModal', () => {
  it('applies a custom overlay class when provided', () => {
    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase()}
        onSave={vi.fn()}
        overlayClassName="z-[2300]"
      />,
    );

    const overlay = screen.getByRole('dialog').parentElement;
    expect(overlay).toBeTruthy();
    expect(overlay?.className).toContain('z-[2300]');
  });

  it('uses the default overlay class when no override is provided', () => {
    render(
      <CaseModal
        isOpen
        onClose={vi.fn()}
        caseData={createCase()}
        onSave={vi.fn()}
      />,
    );

    const overlay = screen.getByRole('dialog').parentElement;
    expect(overlay).toBeTruthy();
    expect(overlay?.className).toContain('z-[1000]');
  });
});
