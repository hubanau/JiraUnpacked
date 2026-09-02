interface LogoProps {
  /** Pixel size of the mark. The wordmark scales with it. */
  size?: number;
  /** Show "Jira Unpacked" beside the mark. */
  withWordmark?: boolean;
  className?: string;
}

/**
 * Brand colours, deliberately fixed rather than themed.
 *
 * A badge does not invert. If the tile followed --ju-primary it would be one
 * colour in light mode and another in dark, which makes it read as two logos —
 * and the favicon, which has no theme at all, could match neither. One artwork
 * everywhere is the point of a solid tile.
 */
// Indigo-500 rather than the light theme's primary (#4f46e5). Because the tile
// is fixed it has to clear 3:1 against BOTH surfaces, and #4f46e5 manages only
// 2.77:1 on the dark one — the badge edge dissolves into the page. This sits
// between the light and dark primaries and clears both (4.47:1 and 3.90:1).
const TILE = '#6366f1';
const KNOCKOUT = '#ffffff';

/**
 * The app mark: a solid tile with three bars knocked out, the top one shifted
 * clear of the other two — "unpacked".
 *
 * Everything about the mark lives in this one file, so changing it is a single
 * edit rather than a hunt. Two properties are worth keeping if it is redrawn:
 *
 * - The tile gives a constant silhouette. Whatever happens to the bars at small
 *   sizes, the badge stays recognisable — which is why this survives the 18px
 *   sidebar where a dot matrix or a fine outline would not.
 * - It is fixed-colour, so the same artwork serves the UI and the favicon.
 */
export function Logo({ size = 24, withWordmark = false, className = '' }: LogoProps) {
  const mark = (
    <svg
      viewBox="0 0 24 24"
      width={size}
      height={size}
      aria-hidden="true"
      focusable="false"
      style={{ flexShrink: 0, display: 'block' }}
    >
      <rect x="1" y="1" width="22" height="22" rx="6.5" fill={TILE} />
      <rect x="7" y="5" width="14" height="3" rx="1.5" fill={KNOCKOUT} />
      <rect x="5" y="10.5" width="14" height="3" rx="1.5" fill={KNOCKOUT} />
      <rect x="5" y="16" width="14" height="3" rx="1.5" fill={KNOCKOUT} />
    </svg>
  );

  if (!withWordmark) {
    return (
      <span className={`inline-flex ${className}`} role="img" aria-label="Jira Unpacked">
        {mark}
      </span>
    );
  }

  return (
    <span className={`inline-flex items-center gap-2.5 ${className}`} role="img" aria-label="Jira Unpacked">
      {mark}
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
