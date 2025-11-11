import type { ClientStatus } from '../../types';

type Props = {
  status?: ClientStatus | null;
  className?: string;
};

function hexToRgba(hex: string | undefined, alpha: number) {
  if (!hex) return `rgba(37, 99, 235, ${alpha})`;
  let normalized = hex.replace('#', '').trim();
  if (normalized.length === 3) {
    normalized = normalized
      .split('')
      .map((c) => c + c)
      .join('');
  }
  if (normalized.length !== 6) return `rgba(37, 99, 235, ${alpha})`;
  const num = Number.parseInt(normalized, 16);
  if (Number.isNaN(num)) return `rgba(37, 99, 235, ${alpha})`;
  const r = (num >> 16) & 255;
  const g = (num >> 8) & 255;
  const b = num & 255;
  return `rgba(${r}, ${g}, ${b}, ${alpha})`;
}

export function ClientStatusBadge({ status, className = '' }: Props) {
  if (!status) return null;
  const baseColor = status.colorHex || '#2563EB';
  const background = hexToRgba(baseColor, 0.12);
  const border = hexToRgba(baseColor, 0.35);
  return (
    <span
      className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs font-medium ${className}`}
      style={{ backgroundColor: background, borderColor: border, color: baseColor }}>
      <span className="h-1.5 w-1.5 rounded-full" style={{ backgroundColor: baseColor }} />
      {status.name}
    </span>
  );
}
