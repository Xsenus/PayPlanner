import type { CSSProperties } from 'react';

export type TaskStickerKey =
  | 'priority.low'
  | 'priority.normal'
  | 'priority.high'
  | 'priority.urgent'
  | 'billing.unbilled'
  | 'billing.invoiced'
  | 'billing.fixed'
  | 'billing.unpaid'
  | 'billing.internal';

export interface TaskStickerStyle {
  label: string;
  shortLabel: string;
  backgroundColor: string;
  textColor: string;
  borderColor: string;
}

export type TaskStickerStyles = Record<TaskStickerKey, TaskStickerStyle>;

export interface TaskStickerDefinition {
  key: TaskStickerKey;
  group: 'priority' | 'billing';
  title: string;
}

export const TASK_STICKER_DEFINITIONS: readonly TaskStickerDefinition[] = [
  { key: 'priority.low', group: 'priority', title: 'Низкий приоритет' },
  { key: 'priority.normal', group: 'priority', title: 'Обычный приоритет' },
  { key: 'priority.high', group: 'priority', title: 'Высокий приоритет' },
  { key: 'priority.urgent', group: 'priority', title: 'Срочный приоритет' },
  { key: 'billing.unbilled', group: 'billing', title: 'Платная задача без счёта' },
  { key: 'billing.invoiced', group: 'billing', title: 'Задача включена в счёт' },
  { key: 'billing.fixed', group: 'billing', title: 'Фиксированный договор' },
  { key: 'billing.unpaid', group: 'billing', title: 'Неоплачиваемая клиентская задача' },
  { key: 'billing.internal', group: 'billing', title: 'Внутренняя задача' },
] as const;

export const DEFAULT_TASK_STICKER_STYLES: TaskStickerStyles = {
  'priority.low': {
    label: 'Низкий',
    shortLabel: 'Низкий',
    backgroundColor: '#f8fafc',
    textColor: '#475569',
    borderColor: '#e2e8f0',
  },
  'priority.normal': {
    label: 'Обычный',
    shortLabel: 'Обычный',
    backgroundColor: '#eff6ff',
    textColor: '#1d4ed8',
    borderColor: '#bfdbfe',
  },
  'priority.high': {
    label: 'Высокий',
    shortLabel: 'Высокий',
    backgroundColor: '#fffbeb',
    textColor: '#b45309',
    borderColor: '#fde68a',
  },
  'priority.urgent': {
    label: 'Срочно',
    shortLabel: 'Срочно',
    backgroundColor: '#fff1f2',
    textColor: '#be123c',
    borderColor: '#fecdd3',
  },
  'billing.unbilled': {
    label: 'ПЛАТНАЯ · НЕ ВЫСТАВЛЕН СЧЁТ',
    shortLabel: 'БЕЗ СЧЁТА',
    backgroundColor: '#ffe4e6',
    textColor: '#9f1239',
    borderColor: '#fda4af',
  },
  'billing.invoiced': {
    label: 'В СЧЁТЕ',
    shortLabel: 'В СЧЁТЕ',
    backgroundColor: '#d1fae5',
    textColor: '#065f46',
    borderColor: '#6ee7b7',
  },
  'billing.fixed': {
    label: 'КЛИЕНТСКАЯ · ФИКСИРОВАННЫЙ ДОГОВОР',
    shortLabel: 'ФИКС. ДОГОВОР',
    backgroundColor: '#ede9fe',
    textColor: '#5b21b6',
    borderColor: '#c4b5fd',
  },
  'billing.unpaid': {
    label: 'НЕОПЛАЧИВАЕМАЯ · НЕ ДЛЯ СЧЁТА',
    shortLabel: 'НЕ ДЛЯ СЧЁТА',
    backgroundColor: '#f1f5f9',
    textColor: '#334155',
    borderColor: '#cbd5e1',
  },
  'billing.internal': {
    label: 'ВНУТРЕННЯЯ · НЕ ДЛЯ СЧЁТА',
    shortLabel: 'НЕ ДЛЯ СЧЁТА',
    backgroundColor: '#f1f5f9',
    textColor: '#334155',
    borderColor: '#cbd5e1',
  },
};

function copyTaskStickerStyle(style: TaskStickerStyle): TaskStickerStyle {
  return { ...style };
}

export function cloneDefaultTaskStickerStyles(): TaskStickerStyles {
  return Object.fromEntries(
    TASK_STICKER_DEFINITIONS.map(({ key }) => [key, copyTaskStickerStyle(DEFAULT_TASK_STICKER_STYLES[key])]),
  ) as TaskStickerStyles;
}

export function normalizeTaskStickerStyles(
  styles?: Partial<Record<TaskStickerKey, Partial<TaskStickerStyle>>> | null,
): TaskStickerStyles {
  return Object.fromEntries(
    TASK_STICKER_DEFINITIONS.map(({ key }) => [
      key,
      {
        ...DEFAULT_TASK_STICKER_STYLES[key],
        ...(styles?.[key] ?? {}),
      },
    ]),
  ) as TaskStickerStyles;
}

export function taskPriorityStickerKey(priority: string): TaskStickerKey {
  switch (priority) {
    case 'low':
      return 'priority.low';
    case 'high':
      return 'priority.high';
    case 'urgent':
      return 'priority.urgent';
    default:
      return 'priority.normal';
  }
}

export function getTaskStickerInlineStyle(style: {
  backgroundColor: string;
  textColor: string;
  borderColor: string;
}): CSSProperties {
  return {
    backgroundColor: style.backgroundColor,
    color: style.textColor,
    borderColor: style.borderColor,
  };
}
