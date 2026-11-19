import { useEffect, useMemo, useState } from 'react';
import { apiService } from '../../services/api';
import { captureAuthSessionIdentity, isAuthSessionIdentityCurrent } from '../../services/authService';

export function ActNumberPreview({ date, ownerScope, enabled = true }: { date: string; ownerScope: string; enabled?: boolean }) {
  const request = useMemo(() => ({ date, ownerScope, enabled }), [date, ownerScope, enabled]);
  const [preview, setPreview] = useState<{ request: typeof request; number: string } | null>(null);
  useEffect(() => {
    let cancelled = false;
    if (!request.enabled || !/^\d{4}-\d{2}-\d{2}$/.test(request.date)) return;
    try {
      const session = captureAuthSessionIdentity();
      void apiService.getActNextNumber(request.date).then((value) => {
        const number = value.nextNumber;
        const maximumNumber = '9223372036854775807';
        if (!cancelled && isAuthSessionIdentityCurrent(session) && value.year === Number(request.date.slice(0, 4))
          && typeof number === 'string' && number.length <= maximumNumber.length && /^[1-9]/.test(number) && !/[^0-9]/.test(number)
          && (number.length < maximumNumber.length || number <= maximumNumber)) {
          setPreview({ request, number });
        }
      }).catch(() => undefined);
    } catch { /* A preview failure leaves saving available. */ }
    return () => { cancelled = true; };
  }, [request]);
  const number = preview?.request === request ? preview.number : null;
  return (
    <span className="flex flex-col gap-1">
      <input aria-label="Номер акта" readOnly value={number ?? 'Присваивается при сохранении'} className="w-full rounded-lg border border-slate-300 bg-slate-50 px-3 py-2 text-sm text-slate-600" />
      {number && <span className="text-xs text-slate-500">Предварительный номер. Итоговый номер присваивается при сохранении.</span>}
    </span>
  );
}
