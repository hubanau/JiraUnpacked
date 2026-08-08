import type { SVGAttributes } from 'react';

interface SpinnerProps extends SVGAttributes<SVGSVGElement> {
  'aria-label'?: string;
}

export function Spinner({ 'aria-label': ariaLabel = 'Loading', className = '', ...rest }: SpinnerProps) {
  return (
    <svg
      {...rest}
      role="status"
      aria-label={ariaLabel}
      className={`animate-spin h-4 w-4 ${className}`}
      viewBox="0 0 24 24"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
    >
      <circle
        className="opacity-25"
        cx="12"
        cy="12"
        r="10"
        stroke="currentColor"
        strokeWidth="4"
      />
      <path
        className="opacity-75"
        fill="currentColor"
        d="M4 12a8 8 0 018-8v4a4 4 0 00-4 4H4z"
      />
    </svg>
  );
}
