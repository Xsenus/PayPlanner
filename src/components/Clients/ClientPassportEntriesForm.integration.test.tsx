import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import {
  ClientPassportEntriesForm,
  type ClientPassportEntryFormValue,
} from './ClientPassportEntriesForm';

const baseEntry: ClientPassportEntryFormValue = {
  series: '36 19',
  number: '664580',
  issuedBy: 'ОУФМС России по Самарской области',
  issueDate: '2019-06-15',
  departmentCode: '630-001',
  birthPlacePassport: 'г. Самара',
  birthPlaceFias: 'Самарская обл, г Самара',
  validFrom: '2019-06-15',
  validTo: '2045-06-15',
};

describe('ClientPassportEntriesForm integration', () => {
  it('renders passport removal as an icon-only action with an accessible label', () => {
    render(
      <ClientPassportEntriesForm
        entries={[baseEntry]}
        canEdit
        onUpdateEntry={vi.fn()}
        onRemoveEntry={vi.fn()}
        onAddEntry={vi.fn()}
      />,
    );

    expect(screen.getByRole('button', { name: 'Удалить паспорт' })).toBeTruthy();
    expect(screen.queryByText(/^Удалить$/)).toBeNull();
  });

  it('hides passport validity dates by default', () => {
    render(
      <ClientPassportEntriesForm
        entries={[baseEntry]}
        canEdit
        onUpdateEntry={vi.fn()}
        onRemoveEntry={vi.fn()}
        onAddEntry={vi.fn()}
      />,
    );

    expect(screen.queryByText('Действует с')).toBeNull();
    expect(screen.queryByText('Действует по')).toBeNull();
    expect(screen.getByText('Дата выдачи')).toBeTruthy();
  });

  it('can restore validity dates through the shared toggle', () => {
    render(
      <ClientPassportEntriesForm
        entries={[baseEntry]}
        canEdit
        showValidityFields
        onUpdateEntry={vi.fn()}
        onRemoveEntry={vi.fn()}
        onAddEntry={vi.fn()}
      />,
    );

    expect(screen.getByText('Действует с')).toBeTruthy();
    expect(screen.getByText('Действует по')).toBeTruthy();
  });
});
