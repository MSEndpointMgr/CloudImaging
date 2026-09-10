import * as React from 'react';
import { Check, Copy } from 'lucide-react';
import { cn } from '../../lib/utils.ts';

interface CopyableIdProps {
  /** The full value written to the clipboard. */
  value: string;
  /** Shortened text to display instead of `value` (e.g. a 12-character SHA-256 prefix). */
  display?: string;
  /** What is being copied, e.g. "device serial number". Used for the button's accessible name. */
  label: string;
  /** Applied to the value text, so callers control mono/size/colour per context. */
  className?: string;
  /** Applied to the wrapper. */
  wrapperClassName?: string;
  /**
   * Let the value wrap across lines instead of truncating to one. Detail views (a certificate
   * thumbprint in a definition list) have the room to show the value in full and are the place
   * an operator goes precisely to read it; only dense table cells need the single-line ellipsis.
   */
  wrap?: boolean;
}

/**
 * A machine identifier (device serial, SHA-256 digest, certificate thumbprint) that an operator
 * can actually get out of the portal.
 *
 * These values are transcribed against physical hardware or pasted into support tickets, so they
 * are both selectable (the app disables text selection globally as chrome-protection) and backed
 * by an explicit copy button — selecting a truncated digest by hand would only ever yield the
 * visible prefix, whereas the button always copies the full value.
 */
export function CopyableId({
  value,
  display,
  label,
  className,
  wrapperClassName,
  wrap = false,
}: CopyableIdProps): React.ReactElement {
  const [copied, setCopied] = React.useState(false);
  const timer = React.useRef<ReturnType<typeof setTimeout> | null>(null);

  React.useEffect(() => () => { if (timer.current) clearTimeout(timer.current); }, []);

  const handleCopy = React.useCallback(
    (event: React.MouseEvent<HTMLButtonElement>) => {
      // Identifiers frequently sit inside clickable rows; copying must not also navigate.
      event.stopPropagation();
      event.preventDefault();
      void (async () => {
        try {
          await navigator.clipboard.writeText(value);
          setCopied(true);
          if (timer.current) clearTimeout(timer.current);
          timer.current = setTimeout(() => setCopied(false), 1500);
        } catch {
          // Clipboard access can be refused by browser policy or an insecure context. The value
          // stays selectable either way, so the operator still has a route to it.
        }
      })();
    },
    [value],
  );

  return (
    <span
      className={cn(
        'group/copy inline-flex max-w-full gap-1',
        // A wrapped value is a block of text, so the button aligns to its first line rather than
        // floating beside the vertical middle of two or three lines.
        wrap ? 'items-start' : 'items-center',
        wrapperClassName,
      )}
    >
      {/* `title` carries the full value only when something is actually hidden: the value can be
          shortened explicitly (the `display` prop, e.g. a 12-character digest prefix) or
          implicitly by CSS truncation in a narrow column. When it wraps, it is all on screen
          already and a tooltip repeating it is just noise. */}
      <span
        className={cn(wrap ? 'break-all' : 'truncate', 'select-text', className)}
        title={wrap ? undefined : value}
      >
        {display ?? value}
      </span>
      <button
        type="button"
        onClick={handleCopy}
        aria-label={`Copy ${label}`}
        className={cn(
          'shrink-0 rounded p-1 text-muted-foreground transition-opacity',
          'hover:bg-accent hover:text-foreground',
          'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background',
          // Hidden until the row is hovered so dense tables stay quiet, but always focusable and
          // revealed on keyboard focus - opacity, unlike `hidden`, keeps it in the tab order.
          'opacity-0 focus-visible:opacity-100 group-hover/copy:opacity-100',
        )}
      >
        {copied
        ? <Check size={16} className="text-emerald-600 dark:text-emerald-400" aria-hidden="true" />
        : <Copy size={16} aria-hidden="true" />}
      </button>
      {/* Confirmation for screen readers; sighted users get the icon swap. */}
      <span aria-live="polite" className="sr-only">
        {copied ? `${label} copied to clipboard` : ''}
      </span>
    </span>
  );
}
