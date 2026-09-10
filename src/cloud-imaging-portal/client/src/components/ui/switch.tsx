import * as React from 'react';
import { cn } from '../../lib/utils';

export interface SwitchProps {
  /** Current state of the switch. */
  checked: boolean;
  /** Called with the *next* state when the user toggles the switch. */
  onCheckedChange: (checked: boolean) => void;
  /**
   * Accessible name. Required: a switch renders no text of its own, so without
   * this it is announced as an unnamed "switch, on/off" control.
   */
  label: string;
  disabled?: boolean;
  className?: string;
}

/**
 * Boolean on/off control used for deployment settings.
 *
 * Prefer this over a hand-rolled `role="switch"` button — the two settings panels
 * that previously inlined this markup had already drifted apart (one carried an
 * accessible name, the other did not).
 */
export function Switch({
  checked,
  onCheckedChange,
  label,
  disabled = false,
  className,
}: SwitchProps): React.ReactElement {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      disabled={disabled}
      onClick={() => onCheckedChange(!checked)}
      className={cn(
        'relative inline-flex h-6 w-11 shrink-0 items-center rounded-full transition-colors',
        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background',
        checked ? 'bg-primary' : 'bg-muted',
        disabled ? 'cursor-not-allowed opacity-50' : 'cursor-pointer',
        className,
      )}
    >
      <span
        className={cn(
          'inline-block h-4 w-4 transform rounded-full bg-white shadow transition-transform',
          checked ? 'translate-x-6' : 'translate-x-1',
        )}
      />
    </button>
  );
}
