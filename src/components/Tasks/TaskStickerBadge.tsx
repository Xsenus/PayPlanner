import {
  createContext,
  useContext,
  useMemo,
  type ReactNode,
} from 'react';
import {
  cloneDefaultTaskStickerStyles,
  getTaskStickerInlineStyle,
  normalizeTaskStickerStyles,
  type TaskStickerKey,
  type TaskStickerStyles,
} from '../../utils/taskStickerStyles';

const TaskStickerStylesContext = createContext<TaskStickerStyles>(cloneDefaultTaskStickerStyles());

export function TaskStickerStylesProvider({
  styles,
  children,
}: {
  styles?: Partial<TaskStickerStyles> | null;
  children: ReactNode;
}) {
  const value = useMemo(() => normalizeTaskStickerStyles(styles), [styles]);
  return (
    <TaskStickerStylesContext.Provider value={value}>
      {children}
    </TaskStickerStylesContext.Provider>
  );
}

export function TaskStickerBadge({
  stickerKey,
  compact = false,
  label,
  labelSuffix = '',
  title,
  className = '',
  icon,
  testId,
}: {
  stickerKey: TaskStickerKey;
  compact?: boolean;
  label?: string;
  labelSuffix?: string;
  title?: string;
  className?: string;
  icon?: ReactNode;
  testId?: string;
}) {
  const style = useContext(TaskStickerStylesContext)[stickerKey];
  const baseLabel = label ?? (compact ? style.shortLabel : style.label);
  const displayLabel = `${baseLabel}${labelSuffix}`;
  const fullTitle = title ?? `${style.label}${labelSuffix}`;

  return (
    <span
      className={`${className} inline-flex max-w-full items-center gap-1 rounded-full border font-semibold ${
        compact ? 'px-2 py-0.5 text-[10px]' : 'px-2.5 py-1 text-[11px] tracking-wide'
      }`}
      style={getTaskStickerInlineStyle(style)}
      data-testid={testId}
      title={fullTitle}
    >
      {icon}
      <span className="truncate">{displayLabel}</span>
    </span>
  );
}
