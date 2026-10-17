import * as React from 'react';
import { createPortal } from 'react-dom';
import { Check, ChevronDown } from 'lucide-react';
import { cn } from '../../lib/utils.ts';
import { Button } from './button.tsx';
import { DateInput } from './date-input.tsx';
import { Label } from './label.tsx';
import { useAnchoredPosition, useDismissOnOutsidePress } from './anchored-position.ts';
import {
  DATE_RANGE_PRESETS,
  isValidRange,
  matchPreset,
  rangeDayCount,
  rangeLabel,
  resolvePreset,
  type DateRange,
  type DateRangePresetKey,
} from '../../lib/dateRange.ts';

interface DateRangeSelectProps {
  value: DateRange;
  onValueChange: (range: DateRange) => void;
  id?: string;
  'aria-label'?: string;
  /** Applied to the positioning wrapper, which is what participates in the parent's flex layout. */
  wrapperClassName?: string;
}

/** Tall enough for the preset list plus the custom footer when it is open. */
const PANEL_HEIGHT = 420;
/**
 * Wide enough for two date fields side by side. Each needs room for the formatted date plus the
 * calendar button, and a browser clips its date text rather than letting the field scroll.
 */
const PANEL_MIN_WIDTH = 340;

/**
 * Report date range picker: named presets, with two date fields revealed for a custom range.
 *
 * A range is one idea, so it gets one control rather than the two independent date inputs the
 * reports used to show. The presets matter more than the fields: every report opens on the last
 * 30 days, and "last 7 days" was previously only reachable by typing a date.
 *
 * The custom fields stay inside the popover with an explicit Apply, because a half-entered range
 * is not a range: applying on every keystroke would refetch against a bound the operator is still
 * typing.
 */
export function DateRangeSelect({
  value,
  onValueChange,
  id,
  'aria-label': ariaLabel,
  wrapperClassName,
}: DateRangeSelectProps): React.ReactElement {
  const generatedId = React.useId();
  const triggerId = id ?? `${generatedId}-trigger`;
  const listId = `${generatedId}-list`;

  const [open, setOpen] = React.useState(false);
  const triggerRef = React.useRef<HTMLButtonElement>(null);
  const panelRef = React.useRef<HTMLDivElement>(null);

  const selectedPreset = matchPreset(value);
  const [activePreset, setActivePreset] = React.useState<DateRangePresetKey>(selectedPreset);
  const [draft, setDraft] = React.useState<DateRange>(value);

  const position = useAnchoredPosition(triggerRef, open, {
    preferredHeight: PANEL_HEIGHT,
    minWidth: PANEL_MIN_WIDTH,
  });

  const close = React.useCallback(() => setOpen(false), []);
  useDismissOnOutsidePress([triggerRef, panelRef], open, close);

  const openPanel = (): void => {
    setActivePreset(selectedPreset);
    setDraft(value);
    setOpen(true);
  };

  const choosePreset = (key: DateRangePresetKey): void => {
    setActivePreset(key);
    if (key === 'custom') return;

    const resolved = resolvePreset(key);
    if (!resolved) return;
    onValueChange(resolved);
    setOpen(false);
    triggerRef.current?.focus();
  };

  const applyCustom = (): void => {
    if (!isValidRange(draft)) return;
    onValueChange(draft);
    setOpen(false);
    triggerRef.current?.focus();
  };

  const cancel = (): void => {
    setOpen(false);
    triggerRef.current?.focus();
  };

  const handleKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>): void => {
    if (event.key === 'Escape' && open) {
      event.preventDefault();
      // Escape closes this panel only. Without stopping it the same keypress reaches the dismiss
      // handler of whatever surface the trigger lives in and tears that down too.
      event.stopPropagation();
      cancel();
      return;
    }
    if (event.key === 'ArrowDown' || event.key === 'Enter' || event.key === ' ') {
      if (open) return;
      event.preventDefault();
      openPanel();
    }
  };

  const days = rangeDayCount(draft);
  const customInvalid = activePreset === 'custom' && !isValidRange(draft);

  return (
    <div className={cn('relative', wrapperClassName)}>
      <button
        ref={triggerRef}
        id={triggerId}
        type="button"
        role="combobox"
        aria-expanded={open}
        aria-haspopup="dialog"
        aria-controls={open ? listId : undefined}
        aria-label={ariaLabel}
        onClick={() => (open ? cancel() : openPanel())}
        onKeyDown={handleKeyDown}
        className={cn(
          'flex h-8 w-full items-center justify-between gap-2 rounded-md border border-input bg-background pl-3 pr-2 text-sm shadow-sm',
          'transition-colors hover:bg-accent/40',
          'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background',
        )}
      >
        <span className="truncate">{rangeLabel(value, selectedPreset)}</span>
        <ChevronDown
          aria-hidden="true"
          className={cn(
            'h-4 w-4 shrink-0 text-muted-foreground transition-transform duration-150',
            open && 'rotate-180',
          )}
        />
      </button>

      {open && position &&
        createPortal(
          <div
            ref={panelRef}
            id={listId}
            role="dialog"
            aria-label="Choose a date range"
            style={{
              position: 'fixed',
              left: position.left,
              width: position.width,
              ...(position.top !== undefined ? { top: position.top } : { bottom: position.bottom }),
            }}
            className="z-50 overflow-hidden rounded-lg border border-border bg-popover text-popover-foreground shadow-xl ring-1 ring-black/5 animate-popover-in"
          >
            <ul role="listbox" aria-label="Date range presets" className="p-1">
              {DATE_RANGE_PRESETS.map(preset => {
                const selected = activePreset === preset.key;
                return (
                  <li key={preset.key}>
                    <button
                      type="button"
                      role="option"
                      aria-selected={selected}
                      onClick={() => choosePreset(preset.key)}
                      className={cn(
                        'relative flex w-full cursor-pointer select-none items-center rounded-md px-2 py-1.5 text-left text-sm',
                        'hover:bg-accent hover:text-accent-foreground',
                        selected && 'bg-accent font-medium text-accent-foreground',
                      )}
                    >
                      {preset.label}
                      {selected && <Check aria-hidden="true" className="absolute right-2 h-4 w-4 text-primary" />}
                    </button>
                  </li>
                );
              })}
            </ul>

            {activePreset === 'custom' && (
              <div className="border-t border-border p-3">
                <div className="flex items-start gap-3">
                  <div className="min-w-0 flex-1 space-y-2">
                    <Label htmlFor={`${generatedId}-from`}>From</Label>
                    <DateInput
                      id={`${generatedId}-from`}
                      value={draft.from}
                      max={draft.to || undefined}
                      onValueChange={from => setDraft(current => ({ ...current, from }))}
                    />
                  </div>
                  <div className="min-w-0 flex-1 space-y-2">
                    <Label htmlFor={`${generatedId}-to`}>To</Label>
                    <DateInput
                      id={`${generatedId}-to`}
                      value={draft.to}
                      min={draft.from || undefined}
                      onValueChange={to => setDraft(current => ({ ...current, to }))}
                    />
                  </div>
                </div>
                <p className={cn('mt-2 text-xs', customInvalid ? 'text-destructive' : 'text-muted-foreground')}>
                  {customInvalid
                    ? 'Choose a start and end date, with the end on or after the start.'
                    : `${days} ${days === 1 ? 'day' : 'days'} selected`}
                </p>
                <div className="mt-3 flex justify-end gap-2">
                  <Button variant="secondary" onClick={cancel}>Cancel</Button>
                  <Button onClick={applyCustom} disabled={customInvalid}>Apply</Button>
                </div>
              </div>
            )}
          </div>,
          document.body,
        )}
    </div>
  );
}
