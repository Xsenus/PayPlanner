import { Check, ChevronDown, Search, X } from 'lucide-react';
import {
  Children,
  Fragment,
  forwardRef,
  isValidElement,
  useCallback,
  useEffect,
  useId,
  useMemo,
  useRef,
  useState,
  type ChangeEvent,
  type ReactNode,
  type SelectHTMLAttributes,
} from 'react';
import { createPortal } from 'react-dom';

interface SelectOption {
  value: string;
  label: ReactNode;
  searchLabel: string;
  disabled: boolean;
  group?: string;
}

export interface SystemSelectOptionView {
  value: string;
  label: ReactNode;
  searchLabel: string;
  disabled: boolean;
  group?: string;
}

export interface SystemSelectProps extends SelectHTMLAttributes<HTMLSelectElement> {
  /** Enables search explicitly. By default it appears for lists longer than 12 items. */
  searchable?: boolean;
  searchPlaceholder?: string;
  emptyMessage?: string;
  menuClassName?: string;
  renderOption?: (option: SystemSelectOptionView) => ReactNode;
  renderValue?: (option: SystemSelectOptionView) => ReactNode;
}

interface MenuPosition {
  left: number;
  top?: number;
  bottom?: number;
  width: number;
  maxHeight: number;
}

const OPEN_EVENT = 'payplanner:system-select-open';
const SEARCH_THRESHOLD = 12;
const VIEWPORT_GAP = 8;
const MIN_MENU_HEIGHT = 120;
const MAX_MENU_HEIGHT = 360;

function nodeToText(node: ReactNode): string {
  if (typeof node === 'string' || typeof node === 'number') {
    return String(node);
  }

  if (Array.isArray(node)) {
    return node.map(nodeToText).join(' ');
  }

  if (isValidElement<{ children?: ReactNode }>(node)) {
    return nodeToText(node.props.children);
  }

  return '';
}

function collectOptions(children: ReactNode, group?: string): SelectOption[] {
  const options: SelectOption[] = [];

  Children.forEach(children, (child) => {
    if (!isValidElement(child)) {
      return;
    }

    if (child.type === Fragment) {
      options.push(...collectOptions((child.props as { children?: ReactNode }).children, group));
      return;
    }

    if (child.type === 'optgroup') {
      const props = child.props as { children?: ReactNode; label?: string };
      options.push(...collectOptions(props.children, props.label));
      return;
    }

    if (child.type !== 'option') {
      return;
    }

    const props = child.props as {
      children?: ReactNode;
      value?: string | number | readonly string[];
      disabled?: boolean;
    };
    const searchLabel = nodeToText(props.children).trim();
    const optionValue = props.value === undefined ? searchLabel : String(props.value);

    options.push({
      value: optionValue,
      label: props.children,
      searchLabel,
      disabled: Boolean(props.disabled),
      group,
    });
  });

  return options;
}

function assignRef<T>(ref: React.ForwardedRef<T>, value: T | null) {
  if (typeof ref === 'function') {
    ref(value);
  } else if (ref) {
    ref.current = value;
  }
}

function normalizeSearch(value: string) {
  return value.trim().toLocaleLowerCase('ru').replace(/ё/g, 'е').replace(/\s+/g, ' ');
}

export const SystemSelect = forwardRef<HTMLSelectElement, SystemSelectProps>(function SystemSelect(
  {
    children,
    className = '',
    disabled = false,
    value,
    defaultValue,
    onChange,
    onBlur,
    onFocus,
    multiple,
    size,
    searchable,
    searchPlaceholder = 'Поиск по списку',
    emptyMessage = 'Ничего не найдено',
    menuClassName = '',
    renderOption,
    renderValue,
    id,
    title,
    ...nativeProps
  },
  forwardedRef,
) {
  const instanceId = useId();
  const listboxId = `${instanceId}-listbox`;
  const buttonRef = useRef<HTMLButtonElement | null>(null);
  const nativeRef = useRef<HTMLSelectElement | null>(null);
  const searchRef = useRef<HTMLInputElement | null>(null);
  const optionRefs = useRef(new Map<string, HTMLButtonElement>());
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [activeValue, setActiveValue] = useState('');
  const [uncontrolledValue, setUncontrolledValue] = useState(() => {
    if (Array.isArray(defaultValue)) {
      return String(defaultValue[0] ?? '');
    }
    return defaultValue === undefined ? '' : String(defaultValue);
  });
  const [menuPosition, setMenuPosition] = useState<MenuPosition | null>(null);

  const options = useMemo(() => collectOptions(children), [children]);
  const isControlled = value !== undefined;
  const selectedValue = isControlled
    ? Array.isArray(value) ? String(value[0] ?? '') : String(value ?? '')
    : uncontrolledValue || (defaultValue === undefined ? options[0]?.value ?? '' : '');
  const selectedOption = useMemo(
    () => options.find((option) => option.value === selectedValue) ?? null,
    [options, selectedValue],
  );
  const hasSearch = searchable ?? options.length > SEARCH_THRESHOLD;
  const normalizedQuery = normalizeSearch(query);
  const filteredOptions = useMemo(
    () => normalizedQuery
      ? options.filter((option) => normalizeSearch(option.searchLabel).includes(normalizedQuery))
      : options,
    [normalizedQuery, options],
  );
  const enabledFilteredOptions = useMemo(
    () => filteredOptions.filter((option) => !option.disabled),
    [filteredOptions],
  );

  const setNativeElement = useCallback((element: HTMLSelectElement | null) => {
    nativeRef.current = element;
    assignRef(forwardedRef, element);
  }, [forwardedRef]);

  const updatePosition = useCallback(() => {
    const button = buttonRef.current;
    if (!button || typeof window === 'undefined') {
      return;
    }

    const rect = button.getBoundingClientRect();
    const viewportWidth = window.innerWidth;
    const viewportHeight = window.innerHeight;
    const availableBelow = viewportHeight - rect.bottom - VIEWPORT_GAP;
    const availableAbove = rect.top - VIEWPORT_GAP;
    const openAbove = availableBelow < 220 && availableAbove > availableBelow;
    const availableHeight = Math.max(openAbove ? availableAbove : availableBelow, MIN_MENU_HEIGHT);
    const availableWidth = Math.max(viewportWidth - VIEWPORT_GAP * 2, 1);
    const width = Math.min(Math.max(rect.width, 220), availableWidth);
    const left = Math.min(
      Math.max(rect.left, VIEWPORT_GAP),
      Math.max(VIEWPORT_GAP, viewportWidth - width - VIEWPORT_GAP),
    );

    setMenuPosition({
      left,
      ...(openAbove
        ? { bottom: viewportHeight - rect.top + 4 }
        : { top: rect.bottom + 4 }),
      width,
      maxHeight: Math.min(MAX_MENU_HEIGHT, availableHeight),
    });
  }, []);

  const closeMenu = useCallback((restoreFocus = false) => {
    setOpen(false);
    setQuery('');
    setMenuPosition(null);
    if (restoreFocus) {
      window.requestAnimationFrame(() => buttonRef.current?.focus());
    }
  }, []);

  const openMenu = useCallback(() => {
    if (disabled) {
      return;
    }

    window.dispatchEvent(new CustomEvent(OPEN_EVENT, { detail: instanceId }));
    setQuery('');
    setActiveValue(selectedOption?.disabled ? '' : selectedValue);
    setOpen(true);
  }, [disabled, instanceId, selectedOption?.disabled, selectedValue]);

  const emitChange = useCallback((nextValue: string) => {
    const nativeSelect = nativeRef.current;
    if (!nativeSelect) {
      return;
    }

    nativeSelect.value = nextValue;
    if (onChange) {
      const nativeEvent = new Event('change', { bubbles: true });
      Object.defineProperties(nativeEvent, {
        target: { configurable: true, value: nativeSelect },
        currentTarget: { configurable: true, value: nativeSelect },
      });
      onChange(nativeEvent as unknown as ChangeEvent<HTMLSelectElement>);
    }
    if (!isControlled) {
      setUncontrolledValue(nativeSelect.value);
    }
  }, [isControlled, onChange]);

  const selectValue = useCallback((nextValue: string) => {
    const option = options.find((item) => item.value === nextValue);
    if (!option || option.disabled) {
      return;
    }
    emitChange(nextValue);
    closeMenu(true);
  }, [closeMenu, emitChange, options]);

  const moveActive = useCallback((direction: 1 | -1) => {
    if (enabledFilteredOptions.length === 0) {
      return;
    }
    const currentIndex = enabledFilteredOptions.findIndex((option) => option.value === activeValue);
    const nextIndex = currentIndex < 0
      ? direction === 1 ? 0 : enabledFilteredOptions.length - 1
      : (currentIndex + direction + enabledFilteredOptions.length) % enabledFilteredOptions.length;
    setActiveValue(enabledFilteredOptions[nextIndex].value);
  }, [activeValue, enabledFilteredOptions]);

  useEffect(() => {
    if (!open) {
      return;
    }

    updatePosition();
    const handleViewportChange = () => updatePosition();
    const handlePointerDown = (event: MouseEvent) => {
      const target = event.target as Node;
      if (!buttonRef.current?.contains(target) && !document.getElementById(listboxId)?.contains(target)) {
        closeMenu();
      }
    };
    const handleOtherSelectOpen = (event: Event) => {
      if ((event as CustomEvent<string>).detail !== instanceId) {
        closeMenu();
      }
    };

    window.addEventListener('resize', handleViewportChange);
    window.addEventListener('scroll', handleViewportChange, true);
    window.addEventListener(OPEN_EVENT, handleOtherSelectOpen);
    document.addEventListener('mousedown', handlePointerDown);
    return () => {
      window.removeEventListener('resize', handleViewportChange);
      window.removeEventListener('scroll', handleViewportChange, true);
      window.removeEventListener(OPEN_EVENT, handleOtherSelectOpen);
      document.removeEventListener('mousedown', handlePointerDown);
    };
  }, [closeMenu, instanceId, listboxId, open, updatePosition]);

  useEffect(() => {
    if (!open) {
      return;
    }

    if (hasSearch) {
      searchRef.current?.focus();
    } else {
      optionRefs.current.get(activeValue)?.focus();
    }
  }, [activeValue, hasSearch, open]);

  useEffect(() => {
    if (!open || !activeValue) {
      return;
    }
    const activeElement = optionRefs.current.get(activeValue);
    if (typeof activeElement?.scrollIntoView === 'function') {
      activeElement.scrollIntoView({ block: 'nearest' });
    }
  }, [activeValue, open]);

  if (multiple || (typeof size === 'number' && size > 1)) {
    return (
      <select
        {...nativeProps}
        ref={setNativeElement}
        id={id}
        title={title}
        className={className}
        disabled={disabled}
        multiple={multiple}
        size={size}
        value={value}
        defaultValue={defaultValue}
        onChange={onChange}
        onBlur={onBlur}
        onFocus={onFocus}
      >
        {children}
      </select>
    );
  }

  const visibleLabel = selectedOption
    ? renderValue?.(selectedOption) ?? selectedOption.label
    : 'Выберите значение';
  const ariaLabel = nativeProps['aria-label'];
  const activeDescendant = open && activeValue ? `${listboxId}-${encodeURIComponent(activeValue)}` : undefined;

  return (
    <>
      <select
        {...nativeProps}
        ref={setNativeElement}
        className="pointer-events-none absolute h-px w-px overflow-hidden opacity-0"
        disabled={disabled}
        name={nativeProps.name}
        required={nativeProps.required}
        value={isControlled ? value : uncontrolledValue}
        onChange={(event) => {
          onChange?.(event);
          if (!isControlled) {
            setUncontrolledValue(event.currentTarget.value);
          }
        }}
        onBlur={onBlur}
        onFocus={onFocus}
        tabIndex={-1}
        aria-hidden="true"
        data-system-select-native="true"
      >
        {children}
      </select>

      <button
        ref={buttonRef}
        id={id}
        type="button"
        title={title}
        disabled={disabled}
        role="combobox"
        aria-label={ariaLabel ? `${ariaLabel}. Выбрано: ${selectedOption?.searchLabel ?? 'Выберите значение'}` : undefined}
        aria-expanded={open}
        aria-controls={listboxId}
        aria-haspopup="listbox"
        aria-activedescendant={activeDescendant}
        aria-required={nativeProps.required || undefined}
        aria-invalid={nativeProps['aria-invalid']}
        aria-describedby={nativeProps['aria-describedby']}
        value={selectedValue}
        onPointerDown={nativeProps.onPointerDown as unknown as React.PointerEventHandler<HTMLButtonElement>}
        onClick={() => open ? closeMenu() : openMenu()}
        onChange={(event) => emitChange(event.currentTarget.value)}
        onBlur={(event) => {
          if (!open) {
            onBlur?.(event as unknown as React.FocusEvent<HTMLSelectElement>);
          }
        }}
        onFocus={(event) => onFocus?.(event as unknown as React.FocusEvent<HTMLSelectElement>)}
        onKeyDown={(event) => {
          if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            event.preventDefault();
            if (!open) {
              openMenu();
            } else {
              moveActive(event.key === 'ArrowDown' ? 1 : -1);
            }
          } else if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            if (!open) {
              openMenu();
            } else if (activeValue) {
              selectValue(activeValue);
            }
          } else if (event.key === 'Escape' && open) {
            event.preventDefault();
            closeMenu(true);
          }
        }}
        className={`flex min-h-[38px] max-w-full items-center justify-between gap-2 rounded-lg border border-slate-300 bg-white px-3 py-2 text-left text-sm text-slate-700 outline-none transition hover:border-slate-400 focus:border-blue-500 focus:ring-2 focus:ring-blue-100 disabled:cursor-not-allowed disabled:border-slate-200 disabled:bg-slate-100 disabled:text-slate-400 ${className}`}
      >
        <span className={`min-w-0 flex-1 ${renderValue ? '' : 'truncate'}`}>{visibleLabel}</span>
        <ChevronDown className={`h-4 w-4 shrink-0 text-slate-400 transition-transform ${open ? 'rotate-180' : ''}`} />
      </button>

      {open && menuPosition && typeof document !== 'undefined'
        ? createPortal(
          <div
            id={listboxId}
            role="listbox"
            aria-label={ariaLabel ?? title ?? 'Выбор значения'}
            style={{
              left: menuPosition.left,
              top: menuPosition.top,
              bottom: menuPosition.bottom,
              width: menuPosition.width,
              maxHeight: menuPosition.maxHeight,
            }}
            className={`fixed z-[5000] flex overflow-hidden rounded-xl border border-slate-200 bg-white shadow-2xl ring-1 ring-slate-900/5 ${menuClassName}`}
            onKeyDown={(event) => {
              if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
                event.preventDefault();
                moveActive(event.key === 'ArrowDown' ? 1 : -1);
              } else if (event.key === 'Enter' && activeValue) {
                event.preventDefault();
                selectValue(activeValue);
              } else if (event.key === 'Escape') {
                event.preventDefault();
                closeMenu(true);
              }
            }}
          >
            <div className="flex min-h-0 w-full flex-col p-1.5">
              {hasSearch ? (
                <div className="relative mb-1 border-b border-slate-100 pb-1.5">
                  <Search className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-[calc(50%+3px)] text-slate-400" />
                  <input
                    ref={searchRef}
                    value={query}
                    onChange={(event) => {
                      setQuery(event.target.value);
                      const firstEnabled = options.find(
                        (option) => !option.disabled && normalizeSearch(option.searchLabel).includes(normalizeSearch(event.target.value)),
                      );
                      setActiveValue(firstEnabled?.value ?? '');
                    }}
                    placeholder={searchPlaceholder}
                    className="h-9 w-full rounded-lg border border-slate-200 bg-slate-50 py-2 pl-9 pr-8 text-sm text-slate-700 outline-none placeholder:text-slate-400 focus:border-blue-400 focus:bg-white focus:ring-2 focus:ring-blue-100"
                  />
                  {query ? (
                    <button
                      type="button"
                      onClick={() => {
                        setQuery('');
                        setActiveValue(selectedValue);
                        searchRef.current?.focus();
                      }}
                      className="absolute right-1.5 top-1/2 -translate-y-[calc(50%+3px)] rounded-md p-1 text-slate-400 hover:bg-slate-200 hover:text-slate-700"
                      aria-label="Очистить поиск"
                    >
                      <X className="h-4 w-4" />
                    </button>
                  ) : null}
                </div>
              ) : null}

              <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain">
                {filteredOptions.length === 0 ? (
                  <div className="px-3 py-8 text-center text-sm text-slate-500">{emptyMessage}</div>
                ) : (
                  filteredOptions.map((option, index) => {
                    const previousGroup = index > 0 ? filteredOptions[index - 1].group : undefined;
                    const showGroup = option.group && option.group !== previousGroup;
                    const isSelected = option.value === selectedValue;
                    const isActive = option.value === activeValue;
                    return (
                      <Fragment key={`${option.group ?? ''}:${option.value}:${index}`}>
                        {showGroup ? (
                          <div className="px-3 pb-1 pt-2 text-[11px] font-semibold uppercase tracking-wide text-slate-400">
                            {option.group}
                          </div>
                        ) : null}
                        <button
                          ref={(element) => {
                            if (element) {
                              optionRefs.current.set(option.value, element);
                            } else {
                              optionRefs.current.delete(option.value);
                            }
                          }}
                          id={`${listboxId}-${encodeURIComponent(option.value)}`}
                          type="button"
                          role="option"
                          aria-label={option.searchLabel}
                          aria-selected={isSelected}
                          disabled={option.disabled}
                          title={option.searchLabel}
                          tabIndex={isActive ? 0 : -1}
                          onMouseEnter={() => !option.disabled && setActiveValue(option.value)}
                          onClick={() => selectValue(option.value)}
                          className={`flex w-full items-center justify-between gap-3 rounded-lg px-3 py-2 text-left text-sm outline-none transition ${
                            option.disabled
                              ? 'cursor-not-allowed text-slate-300'
                              : isActive
                                ? 'bg-blue-50 text-blue-800'
                                : 'text-slate-700 hover:bg-slate-50'
                          }`}
                        >
                          <span className={`min-w-0 flex-1 ${renderOption ? '' : 'truncate'}`}>
                            {renderOption?.(option) ?? option.label}
                          </span>
                          {isSelected ? <Check className="h-4 w-4 shrink-0 text-blue-600" /> : null}
                        </button>
                      </Fragment>
                    );
                  })
                )}
              </div>
            </div>
          </div>,
          document.body,
        )
        : null}
    </>
  );
});
