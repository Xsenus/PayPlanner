import { SystemSelect } from '../ui/SystemSelect';

/** Коды совпадают с серверными (`VatRates`): none — «без НДС», 0 — нулевая ставка. */
const VAT_RATE_OPTIONS: Array<{ value: string; label: string }> = [
  { value: '', label: 'Не задана' },
  { value: 'none', label: 'Без НДС' },
  { value: '0', label: '0%' },
  { value: '5', label: '5%' },
  { value: '7', label: '7%' },
  { value: '10', label: '10%' },
  { value: '20', label: '20%' },
  { value: '22', label: '22%' },
];

interface VatRateSelectProps {
  value: string | null | undefined;
  onChange: (value: string) => void;
  disabled?: boolean;
  className?: string;
}

/** Выбор ставки НДС «в том числе» для позиции счёта или акта. Пустое значение — ставка не задана. */
export function VatRateSelect({ value, onChange, disabled, className }: VatRateSelectProps) {
  return (
    <SystemSelect
      aria-label="Ставка НДС"
      data-testid="line-item-vat-rate"
      value={value ?? ''}
      disabled={disabled}
      onChange={(event) => onChange(event.target.value)}
      className={className ?? 'w-full rounded-lg border border-slate-300 px-2 py-2 text-sm'}
    >
      {VAT_RATE_OPTIONS.map((option) => (
        <option key={option.value} value={option.value}>
          {option.label}
        </option>
      ))}
    </SystemSelect>
  );
}
