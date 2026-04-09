const COMMON_FIELD_LABELS: Record<string, string> = {
  Name: 'Название',
  FullName: 'Полное имя',
  Description: 'Описание',
  Comment: 'Комментарий',
  Type: 'Тип',
  Status: 'Статус',
  Amount: 'Сумма',
  Number: 'Номер',
  Email: 'Email',
  Phone: 'Телефон',
  Phones: 'Телефоны',
  Address: 'Адрес',
  AddressLine: 'Адрес',
  ResponsibleUserId: 'Ответственный',
  CounterpartyName: 'Контрагент',
  ClientId: 'Клиент',
  ExpenseClientId: 'Клиент расхода',
  ClientCaseId: 'Дело',
  ContractId: 'Договор',
  ActId: 'Акт',
  InvoiceId: 'Счет',
  DueDate: 'Срок оплаты',
  IssueDate: 'Дата выставления',
  PaymentDate: 'Дата оплаты',
  PaidDate: 'Дата оплаты',
  StartDate: 'Дата начала',
  EndDate: 'Дата окончания',
  IsDeleted: 'Состояние удаления',
};

const OBJECT_TYPE_FIELD_OVERRIDES: Record<string, Record<string, string>> = {
  client: {
    FullName: 'ФИО клиента',
    Name: 'Имя клиента',
  },
  'client-case': {
    Name: 'Название дела',
    Description: 'Описание дела',
    Status: 'Статус дела',
  },
  payment: {
    CounterpartyName: 'Клиент / поставщик',
    Description: 'Описание платежа',
    Status: 'Статус платежа',
    Type: 'Тип платежа',
  },
  invoice: {
    CounterpartyName: 'Клиент / поставщик',
    Description: 'Описание счета',
    Status: 'Статус счета',
    Type: 'Тип счета',
  },
  act: {
    Description: 'Описание акта',
    Status: 'Статус акта',
  },
  contract: {
    Description: 'Описание договора',
    Status: 'Статус договора',
    Number: 'Номер договора',
  },
  'legal-entity': {
    Name: 'Наименование организации',
  },
  contact: {
    FullName: 'ФИО контакта',
  },
};

export interface ParsedAuditFieldChange {
  field: string;
  oldValue: string | null;
  newValue: string | null;
}

interface ParsedAuditMetadataObject {
  changedFields?: unknown;
  changes?: unknown;
}

function prettifyFieldName(fieldName: string): string {
  return fieldName
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/[_-]+/g, ' ')
    .trim();
}

export function getObjectAuditFieldLabel(objectType: string | null | undefined, fieldName: string): string {
  const normalizedObjectType = objectType?.trim().toLowerCase() ?? '';
  const objectOverrides = OBJECT_TYPE_FIELD_OVERRIDES[normalizedObjectType];
  if (objectOverrides?.[fieldName]) {
    return objectOverrides[fieldName];
  }

  if (COMMON_FIELD_LABELS[fieldName]) {
    return COMMON_FIELD_LABELS[fieldName];
  }

  return prettifyFieldName(fieldName);
}

export function parseAuditChangedFields(metadataJson: string | null | undefined): string[] {
  if (!metadataJson?.trim()) {
    return [];
  }

  try {
    const parsed = JSON.parse(metadataJson);

    if (Array.isArray(parsed)) {
      return parsed
        .filter((item): item is string => typeof item === 'string' && item.trim().length > 0)
        .map((item) => item.trim());
    }

    if (parsed && typeof parsed === 'object') {
      const objectMetadata = parsed as ParsedAuditMetadataObject;
      if (Array.isArray(objectMetadata.changedFields)) {
        return objectMetadata.changedFields
          .filter((item): item is string => typeof item === 'string' && item.trim().length > 0)
          .map((item) => item.trim());
      }
    }

    return [];
  } catch {
    return [];
  }
}

export function parseAuditFieldChanges(metadataJson: string | null | undefined): ParsedAuditFieldChange[] {
  if (!metadataJson?.trim()) {
    return [];
  }

  try {
    const parsed = JSON.parse(metadataJson);
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return [];
    }

    const objectMetadata = parsed as ParsedAuditMetadataObject;
    if (!Array.isArray(objectMetadata.changes)) {
      return [];
    }

    return objectMetadata.changes
      .filter((item): item is { field?: unknown; oldValue?: unknown; newValue?: unknown } => Boolean(item) && typeof item === 'object')
      .map((item) => {
        const field = typeof item.field === 'string' ? item.field.trim() : '';
        if (!field) {
          return null;
        }

        const oldValue = typeof item.oldValue === 'string' && item.oldValue.trim().length > 0
          ? item.oldValue.trim()
          : null;
        const newValue = typeof item.newValue === 'string' && item.newValue.trim().length > 0
          ? item.newValue.trim()
          : null;

        return { field, oldValue, newValue };
      })
      .filter((item): item is ParsedAuditFieldChange => item !== null);
  } catch {
    return [];
  }
}

export function formatAuditValue(value: string | null | undefined): string {
  return value?.trim() ? value.trim() : '—';
}

export function formatAuditChangeSummary(objectType: string | null | undefined, metadataJson: string | null | undefined): string | null {
  const changedFields = parseAuditChangedFields(metadataJson).map((fieldName) =>
    getObjectAuditFieldLabel(objectType, fieldName),
  );

  if (changedFields.length === 0) {
    return null;
  }

  if (changedFields.length === 1) {
    return `Изменено поле: ${changedFields[0]}`;
  }

  const visibleFields = changedFields.slice(0, 3).join(', ');
  const remainingCount = changedFields.length - 3;

  return remainingCount > 0
    ? `Изменены поля: ${visibleFields} и ещё ${remainingCount}`
    : `Изменены поля: ${visibleFields}`;
}
