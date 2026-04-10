import { useState } from 'react';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { ClientAddressEntriesForm, type ClientAddressEntryFormValue } from './ClientAddressEntriesForm';
import type { DadataAddressSuggestion } from '../../types';

const baseEntry: ClientAddressEntryFormValue = {
  type: 'legal',
  additionalTypes: [],
  fullAddress: '633208, Новосибирская обл, г Искитим, Южный мкр, д 37, кв 19',
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
      unrestrictedValue: '633208, Новосибирская обл, г Искитим, Южный мкр, д 37, кв 19',
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
        dateInputClassName="date-input"
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

    const suggestionButton = screen.getByRole('button', {
      name: '633208, Новосибирская обл, г Искитим, Южный мкр, д 37, кв 19',
    });

    await user.click(suggestionButton);

    expect(onApplySuggestion).toHaveBeenCalledWith(0, fullSuggestion);
  });

  it('keeps full address input as the primary editable field and schedules lookup from it', async () => {
    const user = userEvent.setup();
    const onScheduleSuggestions = vi.fn();

    function Harness() {
      const [entries, setEntries] = useState<ClientAddressEntryFormValue[]>([baseEntry]);

      return (
        <ClientAddressEntriesForm
          entries={entries}
          canEdit
          dateInputClassName="date-input"
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

    const fullAddressInput = screen.getByDisplayValue('633208, Новосибирская обл, г Искитим, Южный мкр, д 37, кв 19');

    await user.clear(fullAddressInput);
    await user.type(fullAddressInput, '633208, Новосибирская обл, г Искитим, Южный мкр, д 38, кв 12');

    expect(screen.getByDisplayValue('633208, Новосибирская обл, г Искитим, Южный мкр, д 38, кв 12')).toBeTruthy();
    expect(onScheduleSuggestions).toHaveBeenLastCalledWith(
      0,
      '633208, Новосибирская обл, г Искитим, Южный мкр, д 38, кв 12',
    );
  });
});
