interface LogoProps {
  /** Pixel size of the mark. The wordmark scales with it. */
  size?: number;
  /** Show "Jira Unpacked" beside the mark. */
  withWordmark?: boolean;
  className?: string;
}

/**
 * The app mark: three stacked plates with the top one lifted clear — "unpacked".
 *
 * Everything about the mark lives in this one file, so changing it is a single
 * edit rather than a hunt. Two properties are load-bearing and worth keeping if
 * it is ever redrawn:
 *
 * - It uses `currentColor`, so it inherits the surrounding text colour and works
 *   in both themes without a second asset.
 * - Stroke width scales with size. A mark drawn for 96px goes muddy at 20px in a
 *   sidebar, which is exactly where a logo has to stay legible.
 */
export function Logo({ size = 24, withWordmark = false, className = '' }: LogoProps) {
  // Thicker relative stroke as the mark shrinks; below ~28px a hairline disappears.
  const stroke = size <= 28 ? 2.4 : size <= 56 ? 2 : 1.8;

  const mark = (
    <svg
      viewBox="0 0 24 24"
      width={size}
      height={size}
      fill="none"
      stroke="currentColor"
      strokeWidth={stroke}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
      style={{ flexShrink: 0 }}
    >
      <rect x="3" y="17" width="18" height="4" rx="1.2" />
      <rect x="3" y="11" width="18" height="4" rx="1.2" />
      <rect x="5" y="2" width="18" height="4" rx="1.2" />
    </svg>
  );

  if (!withWordmark) {
    return (
      <span className={`inline-flex text-[var(--ju-primary)] ${className}`} role="img" aria-label="Jira Unpacked">
        {mark}
      </span>
    );
  }

  return (
    <span className={`inline-flex items-center gap-2.5 ${className}`} role="img" aria-label="Jira Unpacked">
      <span className="inline-flex text-[var(--ju-primary)]">{mark}</span>
      <span
        className="font-medium tracking-tight text-[var(--ju-text)]"
        style={{ fontSize: Math.max(13, Math.round(size * 0.58)) }}
        aria-hidden="true"
      >
        Jira Unpacked
      </span>
    </span>
  );
}
