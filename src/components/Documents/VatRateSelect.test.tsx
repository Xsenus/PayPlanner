import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import { VatRateSelect } from './VatRateSelect';

describe('VatRateSelect', () => {
  it('offers rates including "no VAT" and zero rate as different options', () => {
    render(<VatRateSelect value="" onChange={() => undefined} />);

    const select = screen.getByLabelText('Ставка НДС') as HTMLSelectElement;
    const labels = Array.from(select.options).map((option) => option.textContent);
    expect(labels).toEqual(['Не задана', 'Без НДС', '0%', '5%', '7%', '10%', '20%', '22%']);
    expect(Array.from(select.options).map((option) => option.value)).toContain('none');
  });

  it('reports the chosen code', async () => {
    const onChange = vi.fn();
    render(<VatRateSelect value="" onChange={onChange} />);

    await userEvent.setup().selectOptions(screen.getByLabelText('Ставка НДС'), '22');

    expect(onChange).toHaveBeenCalledWith('22');
  });
});
