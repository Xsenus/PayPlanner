import { useState } from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import { ClientAddressEntriesForm, type ClientAddressEntryFormValue } from './ClientAddressEntriesForm';
import type { DadataAddressSuggestion } from '../../types';

const initialFullAddress = '633208, Новосибирская обл, г Искитим, Южный мкр, д 37, кв 19';
const updatedFullAddress = '633208, Новосибирская обл, г Искитим, Южный мкр, д 38, кв 12';

const baseEntry: ClientAddressEntryFormValue = {
  type: 'legal',
  additionalTypes: [],
  fullAddress: initialFullAddress,
  postalCode: '633208',
  region: 'Новосибирская обл',
  district: '',
  city: 'г Искитим',
  settlement: '',
  street: 'Южный мкр',
  house: '37',
  flat: '19',
  validFrom: '',
  validTo: '',
};

const addressTypeOptions = [
  { value: 'legal', label: 'Юридический адрес' },
  { value: 'actual', label: 'Фактический адрес' },
];

describe('ClientAddressEntriesForm integration', () => {
  it('shows full address suggestions and applies the selected suggestion', async () => {
    const user = userEvent.setup();
    const onApplySuggestion = vi.fn().mockResolvedValue(undefined);

    const fullSuggestion: DadataAddressSuggestion = {
      value: 'г Искитим, Южный мкр, д 37',
      unrestrictedValue: initialFullAddress,
      postalCode: '633208',
      regionWithType: 'Новосибирская обл',
      cityWithType: 'г Искитим',
      streetWithType: 'Южный мкр',
      house: '37',
      flat: '19',
    };

    render(
      <ClientAddressEntriesForm
        entries={[baseEntry]}
        canEdit
        addressTypeOptions={addressTypeOptions}
        suggestionsByIndex={{ 0: [fullSuggestion] }}
        onUpdateEntry={vi.fn()}
        onRemoveEntry={vi.fn()}
        onAddEntry={vi.fn()}
        onToggleAdditionalType={vi.fn()}
        onScheduleSuggestions={vi.fn()}
        onFindAddress={vi.fn()}
        onApplySuggestion={onApplySuggestion}
      />,
    );

    const suggestionButton = screen.getByRole('button', { name: initialFullAddress });

    await user.click(suggestionButton);

    expect(onApplySuggestion).toHaveBeenCalledWith(0, fullSuggestion);
  });

  it('keeps full address input as the primary editable field and schedules lookup from it', () => {
    const onScheduleSuggestions = vi.fn();

    function Harness() {
      const [entries, setEntries] = useState<ClientAddressEntryFormValue[]>([baseEntry]);

      return (
        <ClientAddressEntriesForm
          entries={entries}
          canEdit
          addressTypeOptions={addressTypeOptions}
          suggestionsByIndex={{}}
          onUpdateEntry={(index, key, value) => {
            setEntries((prev) =>
              prev.map((entry, entryIndex) =>
                entryIndex === index ? { ...entry, [key]: value } : entry,
              ),
            );
          }}
          onRemoveEntry={vi.fn()}
          onAddEntry={vi.fn()}
          onToggleAdditionalType={vi.fn()}
          onScheduleSuggestions={onScheduleSuggestions}
          onFindAddress={vi.fn()}
          onApplySuggestion={vi.fn()}
        />
      );
    }

    render(<Harness />);

    const fullAddressInput = screen.getByDisplayValue(initialFullAddress);

    fireEvent.change(fullAddressInput, {
      target: {
        value: updatedFullAddress,
      },
    });

    expect(screen.getByDisplayValue(updatedFullAddress)).toBeTruthy();
    expect(onScheduleSuggestions).toHaveBeenLastCalledWith(0, updatedFullAddress);
  });

  it('renders address removal as an icon-only action with an accessible label', () => {
    render(
      <ClientAddressEntriesForm
        entries={[baseEntry]}
        canEdit
        addressTypeOptions={addressTypeOptions}
        suggestionsByIndex={{}}
        onUpdateEntry={vi.fn()}
        onRemoveEntry={vi.fn()}
        onAddEntry={vi.fn()}
        onToggleAdditionalType={vi.fn()}
        onScheduleSuggestions={vi.fn()}
        onFindAddress={vi.fn()}
        onApplySuggestion={vi.fn()}
      />,
    );

    expect(screen.getByRole('button', { name: 'Удалить адрес' })).toBeTruthy();
    expect(screen.queryByText(/^Удалить$/)).toBeNull();
  });

  it('shows additional address types instead of validity dates', () => {
    render(
      <ClientAddressEntriesForm
        entries={[baseEntry]}
        canEdit
        addressTypeOptions={addressTypeOptions}
        suggestionsByIndex={{}}
        onUpdateEntry={vi.fn()}
        onRemoveEntry={vi.fn()}
        onAddEntry={vi.fn()}
        onToggleAdditionalType={vi.fn()}
        onScheduleSuggestions={vi.fn()}
        onFindAddress={vi.fn()}
        onApplySuggestion={vi.fn()}
      />,
    );

    expect(screen.getByText('Дополнительно относится к')).toBeTruthy();
    expect(screen.queryByText('Действует с')).toBeNull();
    expect(screen.queryByText('Действует по')).toBeNull();
  });

  it('can restore validity dates through the shared toggle without changing the form markup', () => {
    render(
      <ClientAddressEntriesForm
        entries={[baseEntry]}
        canEdit
        showValidityFields
        addressTypeOptions={addressTypeOptions}
        suggestionsByIndex={{}}
        onUpdateEntry={vi.fn()}
        onRemoveEntry={vi.fn()}
        onAddEntry={vi.fn()}
        onToggleAdditionalType={vi.fn()}
        onScheduleSuggestions={vi.fn()}
        onFindAddress={vi.fn()}
        onApplySuggestion={vi.fn()}
      />,
    );

    expect(screen.getByText('Действует с')).toBeTruthy();
    expect(screen.getByText('Действует по')).toBeTruthy();
  });
});
