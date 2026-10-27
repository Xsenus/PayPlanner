import { useEffect, useMemo, useState } from 'react';

import { useToast } from '../../hooks/useToast';
import { apiService } from '../../services/api';
import type { DocumentSellerSnapshotInfo, OwnCompanyOption } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { SystemSelect } from '../ui/SystemSelect';
import { formatDateTimeInTimeZone } from '../../utils/timeZoneDate';

interface DocumentSellerPickerProps {
  kind: 'invoice' | 'act' | 'contract';
  documentId: number;
  /** New document: keep selection in the form; never send a seller PUT before creation. */
  draft?: boolean;
  companyId?: number | null;
  branchId?: number | null;
  /** Реквизиты уже зафиксированы в выданном документе — выбор недоступен. */
  frozen?: boolean;
  snapshotInfo?: DocumentSellerSnapshotInfo | null;
  timeZoneId?: string | null;
  canEdit: boolean;
  onChanged?: (selection: { companyId: number | null; branchId: number | null }) => void;
}

const SELECT_CLASS =
  'rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100';

/**
 * Выбор собственной компании и подразделения-продавца для счёта, акта или договора.
 * Показывается только если выбирать есть из чего: несколько компаний или у компании есть подразделения.
 * Пустой выбор означает компанию по умолчанию.
 */
export function DocumentSellerPicker({
  kind,
  documentId,
  draft = false,
  companyId = null,
  branchId = null,
  frozen = false,
  snapshotInfo,
  timeZoneId,
  canEdit,
  onChanged,
}: DocumentSellerPickerProps) {
  const toast = useToast();
  const [options, setOptions] = useState<OwnCompanyOption[] | null>(null);
  const [selection, setSelection] = useState({ companyId, branchId });
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    setSelection({ companyId, branchId });
  }, [companyId, branchId, documentId]);

  useEffect(() => {
    if (frozen && snapshotInfo?.companyName) {
      setOptions([]);
      return;
    }
    let cancelled = false;
    apiService
      .getOwnCompanyOptions()
      .then((items) => {
        if (!cancelled) setOptions(items);
      })
      .catch(() => {
        if (!cancelled) setOptions([]);
      });
    return () => {
      cancelled = true;
    };
  }, [frozen, snapshotInfo?.companyName]);

  const defaultCompany = useMemo(() => options?.find((item) => item.isDefault) ?? null, [options]);
  const effectiveCompany = useMemo(
    () => options?.find((item) => item.id === (selection.companyId ?? defaultCompany?.id)) ?? null,
    [options, selection.companyId, defaultCompany],
  );

  if (!options || (!frozen && options.length < 2 && !options.some((item) => item.branches.length > 0))) {
    return null;
  }

  const save = async (next: { companyId: number | null; branchId: number | null }) => {
    const previous = selection;
    setSelection(next);
    if (draft) {
      onChanged?.(next);
      return;
    }
    setSaving(true);
    try {
      if (kind === 'invoice') {
        await apiService.setInvoiceSeller(documentId, next);
      } else if (kind === 'contract') {
        await apiService.setContractSeller(documentId, next);
      } else {
        await apiService.setActSeller(documentId, next);
      }
      onChanged?.(next);
    } catch (error) {
      setSelection(previous);
      toast.error(extractDisplayErrorMessage(error, 'Не удалось сохранить продавца.'), 'Продавец');
    } finally {
      setSaving(false);
    }
  };

  const branchName = frozen && snapshotInfo
    ? snapshotInfo.branchName
    : effectiveCompany?.branches.find((item) => item.id === selection.branchId)?.name;

  if (frozen || !canEdit) {
    return (
      <p data-testid="document-seller-readonly">
        <span className="text-slate-500">Продавец:</span>{' '}
        {(frozen ? snapshotInfo?.companyName : null) ?? effectiveCompany?.shortName ?? '—'}
        {branchName ? ` · ${branchName}` : ''}
        {frozen ? (
          <span className="ml-1 text-xs text-slate-500">
            {snapshotInfo?.legacyImages
              ? '(сохранён старый снимок реквизитов; изображения могут браться из профиля)'
              : snapshotInfo?.freezeReason === 'on-create'
                ? '(реквизиты и изображения зафиксированы при создании)'
                : '(реквизиты зафиксированы в документе)'}
            {snapshotInfo?.frozenAtUtc ? <time dateTime={snapshotInfo.frozenAtUtc}> · {formatDateTimeInTimeZone(snapshotInfo.frozenAtUtc, timeZoneId)}</time> : ' · дата фиксации неизвестна'}
          </span>
        ) : null}
      </p>
    );
  }

  return (
    <div className="flex flex-wrap items-center gap-2" data-testid="document-seller-picker">
      <span className="text-slate-500">Продавец:</span>
      <SystemSelect
        aria-label="Компания-продавец"
        value={selection.companyId ? String(selection.companyId) : ''}
        disabled={saving}
        onChange={(event) => {
          const value = event.target.value;
          void save({ companyId: value ? Number(value) : null, branchId: null });
        }}
        className={SELECT_CLASS}
      >
        <option value="">{defaultCompany ? `По умолчанию (${defaultCompany.shortName})` : 'По умолчанию'}</option>
        {options?.map((item) => (
          <option key={item.id} value={item.id}>
            {item.shortName}
          </option>
        ))}
      </SystemSelect>
      {effectiveCompany && effectiveCompany.branches.length > 0 ? (
        <SystemSelect
          aria-label="Подразделение-продавец"
          value={selection.branchId ? String(selection.branchId) : ''}
          disabled={saving}
          onChange={(event) => {
            const value = event.target.value;
            void save({ companyId: effectiveCompany.id, branchId: value ? Number(value) : null });
          }}
          className={SELECT_CLASS}
        >
          <option value="">Без подразделения</option>
          {effectiveCompany.branches.map((item) => (
            <option key={item.id} value={item.id}>
              {item.name}
            </option>
          ))}
        </SystemSelect>
      ) : null}
    </div>
  );
}
