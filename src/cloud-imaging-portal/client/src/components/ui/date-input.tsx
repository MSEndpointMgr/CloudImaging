import * as React from 'react';
import { Calendar } from 'lucide-react';
import { cn } from '../../lib/utils.ts';
import { Button } from './button.tsx';
import { Input } from './input.tsx';

interface DateInputProps {
  value: string;
  onValueChange: (value: string) => void;
  id?: string;
  min?: string;
  max?: string;
  'aria-label'?: string;
  className?: string;
}

/**
 * A date field with the portal's own calendar affordance.
 *
 * The browser's built-in indicator is hidden and replaced with a Lucide icon on a ghost button.
 * The native one is drawn by the OS, so it ignores every design token, differs per platform, and
 * is clipped out of existence by any field narrow enough that the formatted date fills it. The
 * button still opens the real picker, so keyboard entry and the OS calendar both survive.
 */
export function DateInput({
  value,
  onValueChange,
  id,
  min,
  max,
  'aria-label': ariaLabel,
  className,
}: DateInputProps): React.ReactElement {
  const inputRef = React.useRef<HTMLInputElement>(null);

  const openPicker = (): void => {
    const input = inputRef.current;
    if (!input) return;
    try {
      input.showPicker();
    } catch {
      // Older engines, or a browser that refuses without direct user activation on the input.
      // Focusing still lets the operator type or press the OS shortcut.
      input.focus();
    }
  };

  return (
    <div className={cn('relative', className)}>
      <Input
        ref={inputRef}
        id={id}
        type="date"
        value={value}
        min={min}
        max={max}
        aria-label={ariaLabel}
        onChange={event => onValueChange(event.target.value)}
        className="w-full pr-8 [&::-webkit-calendar-picker-indicator]:hidden"
      />
      <Button
        type="button"
        variant="ghost"
        size="icon"
        tabIndex={-1}
        aria-label="Open calendar"
        className="absolute right-0 top-0"
        onClick={openPicker}
      >
        <Calendar />
      </Button>
    </div>
  );
}
