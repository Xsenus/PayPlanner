import { SystemCheckbox } from './SystemCheckbox';
import { useEffect, useRef, type InputHTMLAttributes } from 'react';

interface TableSelectCheckboxProps
  extends Omit<InputHTMLAttributes<HTMLInputElement>, 'type'> {
  indeterminate?: boolean;
}

export function TableSelectCheckbox({
  indeterminate = false,
  className = '',
  ...props
}: TableSelectCheckboxProps) {
  const inputRef = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    if (!inputRef.current) return;
    inputRef.current.indeterminate = indeterminate;
  }, [indeterminate]);

  return (
    <SystemCheckbox
      {...props}
      ref={inputRef}
      type="checkbox"
      className={`h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-2 focus:ring-blue-200 ${className}`.trim()}
    />
  );
}
