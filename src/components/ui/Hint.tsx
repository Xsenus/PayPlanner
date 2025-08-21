import { Info } from 'lucide-react';
import { useId } from 'react';

type Props = {
  hint: React.ReactNode;
  children: React.ReactNode;
  className?: string;
  position?: 'top-right' | 'top-left' | 'bottom-right' | 'bottom-left';
  visible?: boolean;
};

export function Hint({ hint, children, className = '', position = 'bottom-left', visible }: Props) {
  const id = useId();

  const pin =
    position === 'top-right'
      ? 'right-2 top-2'
      : position === 'top-left'
      ? 'left-2 top-2'
      : position === 'bottom-right'
      ? 'right-2 bottom-2'
      : 'left-2 bottom-2';

  const bubblePos =
    position === 'top-right'
      ? 'right-2 top-10'
      : position === 'top-left'
      ? 'left-2 top-10'
      : position === 'bottom-right'
      ? 'right-2 -top-2 -translate-y-full'
      : 'left-2 -top-2 -translate-y-full';

  const showControlled = visible
    ? 'opacity-100 translate-y-0 scale-100'
    : 'opacity-0 translate-y-1 scale-[0.98]';
  const showUncontrolled =
    'group-hover:opacity-100 group-hover:translate-y-0 group-hover:scale-100 ' +
    'group-focus-within:opacity-100 group-focus-within:translate-y-0 group-focus-within:scale-100';

  return (
    <div className={`relative group outline-none ${className}`}>
      <div
        id={id}
        role="tooltip"
        className={[
          'pointer-events-none absolute',
          bubblePos,
          'z-50 w-64 sm:w-72 max-w-[85vw]',
          'rounded-xl border border-gray-200 bg-white/95 backdrop-blur shadow-lg p-3',
          'transition-all duration-150 ease-out',
          showControlled,
          visible === undefined ? showUncontrolled : '',
        ].join(' ')}>
        <div className="text-xs leading-5 text-gray-700">{hint}</div>
      </div>

      <span
        className={[
          'absolute',
          pin,
          'z-40',
          'opacity-0 transition-opacity duration-150 ease-out',
          'text-[11px] text-gray-500 bg-white/70 backdrop-blur',
          'px-1.5 py-0.5 rounded-lg border flex items-center gap-1',
          visible ? 'opacity-100' : '',
          visible === undefined ? 'group-hover:opacity-100 group-focus-within:opacity-100' : '',
        ].join(' ')}>
        <Info className="w-3.5 h-3.5" />
        формула
      </span>

      <div aria-describedby={id}>{children}</div>
    </div>
  );
}
