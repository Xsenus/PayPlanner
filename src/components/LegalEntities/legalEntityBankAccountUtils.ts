import type { LegalEntityBankAccount } from '../../types';

const PRIMARY_TYPE = 'Основной';
const SECONDARY_TYPE = 'Дополнительный';

const isPrimaryType = (account: LegalEntityBankAccount) =>
  account.accountType?.trim().toLowerCase() === PRIMARY_TYPE.toLowerCase();

/** Счёт основной и активный: только такой счёт подставляется в документы. */
export const isActivePrimaryBankAccount = (account: LegalEntityBankAccount) =>
  !account.isArchived && (account.isPrimary === true || isPrimaryType(account));

/**
 * Приводит признаки «основной» и «архивный» к согласованному виду:
 * тип «Основной» и флаг isPrimary совпадают, у архивного счёта основным быть нельзя,
 * а основным остаётся не больше одного активного счёта (побеждает отмеченный `preferredIndex`).
 */
export function syncBankAccountPrimary(
  accounts: LegalEntityBankAccount[],
  preferredIndex?: number,
): LegalEntityBankAccount[] {
  const wantsPrimary = (account: LegalEntityBankAccount) =>
    !account.isArchived && (account.isPrimary === true || isPrimaryType(account));

  let winner = -1;
  if (preferredIndex !== undefined && accounts[preferredIndex] !== undefined && wantsPrimary(accounts[preferredIndex])) {
    winner = preferredIndex;
  } else {
    winner = accounts.findIndex(wantsPrimary);
  }

  return accounts.map((account, index) => {
    const primary = index === winner;
    const next: LegalEntityBankAccount = {
      ...account,
      isArchived: Boolean(account.isArchived),
      isPrimary: primary,
    };
    if (primary) {
      next.accountType = PRIMARY_TYPE;
    } else if (isPrimaryType(account)) {
      next.accountType = SECONDARY_TYPE;
    }
    return next;
  });
}

/** Добавляет или заменяет счёт, сохраняя `id` и признак архива заменяемой записи. */
export function upsertBankAccount(
  accounts: LegalEntityBankAccount[],
  index: number | null,
  edited: LegalEntityBankAccount,
): LegalEntityBankAccount[] {
  if (index === null) {
    return syncBankAccountPrimary([...accounts, edited], isPrimaryType(edited) ? accounts.length : undefined);
  }

  const previous = accounts[index];
  const merged: LegalEntityBankAccount = {
    ...previous,
    ...edited,
    id: previous?.id,
    isArchived: previous?.isArchived,
    isPrimary: isPrimaryType(edited),
  };
  return syncBankAccountPrimary(
    accounts.map((item, itemIndex) => (itemIndex === index ? merged : item)),
    isPrimaryType(edited) ? index : undefined,
  );
}

/** Переводит счёт в архив или возвращает из архива. Архивный счёт перестаёт быть основным. */
export function setBankAccountArchived(
  accounts: LegalEntityBankAccount[],
  index: number,
  archived: boolean,
): LegalEntityBankAccount[] {
  return syncBankAccountPrimary(
    accounts.map((item, itemIndex) =>
      itemIndex === index ? { ...item, isArchived: archived, isPrimary: archived ? false : item.isPrimary } : item,
    ),
  );
}
