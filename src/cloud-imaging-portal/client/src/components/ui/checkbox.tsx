import * as React from 'react';
import { Check, Minus } from 'lucide-react';
import { cn } from '../../lib/utils';

export interface CheckboxProps {
  /** True, false, or 'mixed' for a partially selected group. */
  checked: boolean | 'mixed';
  /** Called with the next state; 'mixed' always moves to true. */
  onCheckedChange: (checked: boolean) => void;
  /**
   * Accessible name. Required unless the checkbox sits inside a label that names it
   * (pass `labelledBy` with the id of that text instead).
   */
  label?: string;
  labelledBy?: string;
  disabled?: boolean;
  className?: string;
  id?: string;
}

/**
 * Multi-select control for lists (e.g. picking locations). A Switch reads as "turn a setting on",
 * which is wrong for "include this row", and a switch per row does not scale to hundreds of rows.
 */
export function Checkbox({ checked, onCheckedChange, label, labelledBy, disabled = false, className, id }: CheckboxProps): React.ReactElement {
  const on = checked === true;
  const mixed = checked === 'mixed';
  return (
    <button
      id={id}
      type="button"
      role="checkbox"
      aria-checked={mixed ? 'mixed' : on}
      aria-label={label}
      aria-labelledby={labelledBy}
      disabled={disabled}
      onClick={() => onCheckedChange(!on)}
      className={cn(
        'inline-flex h-4 w-4 shrink-0 items-center justify-center rounded-sm border transition-colors',
        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background',
        on || mixed ? 'border-primary bg-primary text-primary-foreground' : 'border-input bg-background',
        disabled ? 'cursor-not-allowed opacity-50' : 'cursor-pointer',
        className,
      )}
    >
      {on && <Check className="h-3 w-3" strokeWidth={3} aria-hidden="true" />}
      {mixed && <Minus className="h-3 w-3" strokeWidth={3} aria-hidden="true" />}
    </button>
  );
}
