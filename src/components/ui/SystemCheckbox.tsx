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
        className={`pp-system-switch relative inline-flex shrink-0 align-middle ${className}`.trim()}
        style={{ width: 36, height: 20, ...style }}
      >
        <input
          {...props}
          ref={setInputRef}
          type="checkbox"
          role="switch"
          disabled={disabled}
          className="absolute inset-0 z-10 m-0 h-full w-full cursor-pointer opacity-0 disabled:cursor-not-allowed"
        />
        <span
          aria-hidden="true"
          className="pp-system-switch-track pointer-events-none absolute inset-0 rounded-full"
        />
        <span
          aria-hidden="true"
          className="pp-system-switch-thumb pointer-events-none absolute left-0.5 top-0.5 h-4 w-4 rounded-full"
        />
      </span>
    );
  }

  return (
    <span
      data-system-checkbox="checkbox"
      className={`pp-system-checkbox relative inline-flex shrink-0 align-middle ${className}`.trim()}
      style={{ width: 18, height: 18, ...style }}
    >
      <input
        {...props}
        ref={setInputRef}
        type="checkbox"
        role={role}
        disabled={disabled}
        className="absolute inset-0 z-10 m-0 h-full w-full cursor-pointer opacity-0 disabled:cursor-not-allowed"
      />
      <span
        aria-hidden="true"
        className="pp-system-checkbox-box pointer-events-none absolute inset-0 rounded-[5px]"
      />
      <span
        aria-hidden="true"
        className="pp-system-checkbox-check pointer-events-none absolute inset-0 flex items-center justify-center opacity-0"
      >
        <Check className="h-3.5 w-3.5" strokeWidth={3} />
      </span>
      <span
        aria-hidden="true"
        className="pp-system-checkbox-indeterminate pointer-events-none absolute inset-0 flex items-center justify-center opacity-0"
      >
        <Minus className="h-3.5 w-3.5" strokeWidth={3} />
      </span>
    </span>
  );
});
