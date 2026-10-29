import { cn } from '../../lib/utils.ts';

/**
 * A row of joined toggle buttons that each switch on and off independently, such as weekdays.
 * Several can be on at once, so each is a pressed/unpressed button rather than a radio.
 */
export function ToggleGroup<T extends string | number>({ items, isOn, onToggle, disabled = false, invalid = false, className, ...aria }: {
  items: readonly { value: T; label: string; ariaLabel?: string }[];
  isOn: (value: T) => boolean;
  onToggle: (value: T) => void;
  disabled?: boolean;
  /** Marks the group as failing validation; explain why in text next to it. */
  invalid?: boolean;
  className?: string;
  'aria-label': string;
}): React.ReactElement {
  return (
    <div
      role="group"
      aria-label={aria['aria-label']}
      aria-invalid={invalid || undefined}
      className={cn('flex overflow-hidden rounded-md border', invalid ? 'border-destructive/70' : 'border-border', className)}
    >
      {items.map((item) => {
        const on = isOn(item.value);
        return (
          <button
            key={String(item.value)}
            type="button"
            aria-pressed={on}
            aria-label={item.ariaLabel}
            disabled={disabled}
            onClick={() => onToggle(item.value)}
            className={cn(
              // 30px plus the group's 1px border keeps the group at the shared 32px control height.
              'flex h-[30px] min-w-12 flex-1 items-center justify-center text-sm font-medium transition-colors [&+&]:border-l [&+&]:border-border',
              'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50',
              on ? 'bg-primary/10 text-foreground' : 'bg-background text-muted-foreground hover:text-foreground',
            )}
          >
            {item.label}
          </button>
        );
      })}
    </div>
  );
}
