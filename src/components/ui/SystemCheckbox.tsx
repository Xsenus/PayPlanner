import { Check, Minus } from 'lucide-react';
import {
  forwardRef,
  useCallback,
  useEffect,
  useRef,
  type InputHTMLAttributes,
} from 'react';

export interface SystemCheckboxProps extends InputHTMLAttributes<HTMLInputElement> {
  indeterminate?: boolean;
}

/**
 * Unified CRM checkbox. The native input remains interactive and accessible,
 * while the visual layer is fully controlled by the design system.
 */
export const SystemCheckbox = forwardRef<HTMLInputElement, SystemCheckboxProps>(function SystemCheckbox(
  {
    indeterminate,
    className = '',
    style,
    role,
    disabled,
    ...props
  },
  forwardedRef,
) {
  const inputRef = useRef<HTMLInputElement | null>(null);
  const isSwitch = role === 'switch';

  useEffect(() => {
    if (inputRef.current) {
      inputRef.current.indeterminate = Boolean(indeterminate);
    }
  }, [indeterminate]);

  const setInputRef = useCallback((node: HTMLInputElement | null) => {
    inputRef.current = node;
    if (typeof forwardedRef === 'function') forwardedRef(node);
    else if (forwardedRef) forwardedRef.current = node;
  }, [forwardedRef]);

  if (isSwitch) {
    return (
      <span
        data-system-checkbox="switch"
        className={`relative inline-flex shrink-0 align-middle ${className}`.trim()}
        style={{ width: 36, height: 20, ...style }}
      >
        <input
          {...props}
          ref={setInputRef}
          type="checkbox"
          role="switch"
          disabled={disabled}
          className="peer absolute inset-0 z-10 m-0 h-full w-full cursor-pointer opacity-0 disabled:cursor-not-allowed"
        />
        <span
          aria-hidden="true"
          className="pointer-events-none absolute inset-0 rounded-full border border-slate-300 bg-slate-200 shadow-inner transition-colors peer-checked:border-blue-600 peer-checked:bg-blue-600 peer-focus-visible:ring-2 peer-focus-visible:ring-blue-200 peer-focus-visible:ring-offset-1 peer-disabled:opacity-50"
        />
        <span
          aria-hidden="true"
          className="pointer-events-none absolute left-0.5 top-0.5 h-4 w-4 rounded-full bg-white shadow-sm transition-transform peer-checked:translate-x-4 peer-disabled:bg-slate-100"
        />
      </span>
    );
  }

  return (
    <span
      data-system-checkbox="checkbox"
      className={`relative inline-flex shrink-0 align-middle ${className}`.trim()}
      style={{ width: 18, height: 18, ...style }}
    >
      <input
        {...props}
        ref={setInputRef}
        type="checkbox"
        role={role}
        disabled={disabled}
        className="peer absolute inset-0 z-10 m-0 h-full w-full cursor-pointer opacity-0 disabled:cursor-not-allowed"
      />
      <span
        aria-hidden="true"
        className="pointer-events-none absolute inset-0 rounded-[5px] border border-slate-300 bg-white shadow-sm transition-colors peer-checked:border-blue-600 peer-checked:bg-blue-600 peer-indeterminate:border-blue-600 peer-indeterminate:bg-blue-600 peer-focus-visible:ring-2 peer-focus-visible:ring-blue-200 peer-focus-visible:ring-offset-1 peer-disabled:border-slate-200 peer-disabled:bg-slate-100 peer-disabled:opacity-70"
      />
      <span
        aria-hidden="true"
        className="pointer-events-none absolute inset-0 flex items-center justify-center text-white opacity-0 transition-opacity peer-checked:opacity-100 peer-indeterminate:opacity-0"
      >
        <Check className="h-3.5 w-3.5" strokeWidth={3} />
      </span>
      <span
        aria-hidden="true"
        className="pointer-events-none absolute inset-0 flex items-center justify-center text-white opacity-0 transition-opacity peer-indeterminate:opacity-100"
      >
        <Minus className="h-3.5 w-3.5" strokeWidth={3} />
      </span>
    </span>
  );
});
