import * as React from 'react';
import { createPortal } from 'react-dom';
import { ChevronDown, Search } from 'lucide-react';
import { cn } from '../../lib/utils.ts';
import { Button } from './button.tsx';
import { CheckboxIndicator } from './checkbox.tsx';
import { Input } from './input.tsx';
import { useAnchoredPosition, useDismissOnOutsidePress } from './anchored-position.ts';
import { selectTriggerVariants } from './select-trigger.ts';

/** One value of a {@link MultiSelect}. */
export interface MultiSelectOption {
  value: string;
  label: string;
  /** Background classes of a dot before the label, in the colour of the value's badge. */
  dotClassName?: string;
}

export interface MultiSelectProps {
  /** Chosen values. Nothing chosen means every value, so the filter is off. */
  values: readonly string[];
  onValuesChange: (values: string[]) => void;
  options: readonly MultiSelectOption[];
  /** Button text while nothing is chosen, e.g. "All locations". */
  placeholder: string;
  /** Button text once more than two values are chosen, e.g. "3 locations". */
  countLabel: (count: number) => string;
  /** Adds a search row above the list, for lists that grow with the tenant such as locations. */
  searchPlaceholder?: string;
  id?: string;
  'aria-label'?: string;
  /** Applied to the positioning wrapper, which is what takes part in the parent's flex layout. */
  wrapperClassName?: string;
  disabled?: boolean;
}

/** As tall as a `Select` list: nine rows. */
const LIST_MAX_HEIGHT = 288;
/** The list plus the search row, the footer and the panel's padding. */
const PANEL_HEIGHT = LIST_MAX_HEIGHT + 88;

/**
 * Filter picker that takes several values: locations, outcomes, vendors.
 *
 * The button reads like a `Select` (the placeholder while nothing is chosen, up to two names, then
 * a count), and the list stays open while values are ticked, because choosing three locations one
 * reopened list at a time is the chore this replaces. Rows are listbox options with a drawn
 * checkbox; a focusable checkbox inside each option would be announced twice.
 */
export function MultiSelect({
  values,
  onValuesChange,
  options,
  placeholder,
  countLabel,
  searchPlaceholder,
  id,
  'aria-label': ariaLabel,
  wrapperClassName,
  disabled = false,
}: MultiSelectProps): React.ReactElement {
  const generatedId = React.useId();
  const triggerId = id ?? `${generatedId}-trigger`;
  const listId = `${generatedId}-list`;

  const [open, setOpen] = React.useState(false);
  const [query, setQuery] = React.useState('');
  const [activeIndex, setActiveIndex] = React.useState(-1);
  const triggerRef = React.useRef<HTMLButtonElement>(null);
  const panelRef = React.useRef<HTMLDivElement>(null);
  const position = useAnchoredPosition(triggerRef, open, { preferredHeight: PANEL_HEIGHT, panelRef, keepWidth: true });

  const chosen = new Set(values);
  const chosenLabels = options.filter(option => chosen.has(option.value)).map(option => option.label);
  const triggerText = values.length === 0
    ? null
    : values.length <= 2 && chosenLabels.length === values.length ? chosenLabels.join(', ') : countLabel(values.length);

  const needle = query.trim().toLocaleLowerCase();
  const visible = needle ? options.filter(option => option.label.toLocaleLowerCase().includes(needle)) : options;

  const close = React.useCallback(() => {
    setOpen(false);
    setQuery('');
    setActiveIndex(-1);
  }, []);
  useDismissOnOutsidePress([triggerRef, panelRef], open, close);

  React.useEffect(() => {
    if (!open || activeIndex < 0) return;
    document.getElementById(`${listId}-${activeIndex}`)?.scrollIntoView?.({ block: 'nearest' });
  }, [open, activeIndex, listId]);

  const openList = (): void => {
    if (disabled) return;
    setActiveIndex(options.length > 0 ? 0 : -1);
    setOpen(true);
  };

  const closeAndFocus = (): void => {
    close();
    triggerRef.current?.focus();
  };

  // Chosen values keep the order of the options, so the button and the chips read the same way.
  const toggle = (value: string): void => {
    onValuesChange(chosen.has(value)
      ? values.filter(v => v !== value)
      : options.filter(option => option.value === value || chosen.has(option.value)).map(option => option.value));
  };

  const toggleActive = (): void => {
    const option = visible[activeIndex];
    if (option) toggle(option.value);
  };

  const handleKeyDown = (event: React.KeyboardEvent<HTMLElement>): void => {
    switch (event.key) {
      case 'ArrowDown':
      case 'ArrowUp': {
        event.preventDefault();
        if (!open) {
          openList();
          return;
        }
        const step = event.key === 'ArrowDown' ? 1 : -1;
        setActiveIndex(index => Math.min(visible.length - 1, Math.max(0, index + step)));
        return;
      }
      case 'Home':
      case 'End':
        if (!open) return;
        event.preventDefault();
        setActiveIndex(event.key === 'Home' ? 0 : visible.length - 1);
        return;
      case 'Enter':
        event.preventDefault();
        if (open) toggleActive();
        else openList();
        return;
      case ' ':
        // In the search row a space is part of what is typed.
        if (open && searchPlaceholder) return;
        event.preventDefault();
        if (open) toggleActive();
        else openList();
        return;
      case 'Escape':
        if (!open) return;
        event.preventDefault();
        // Escape closes the list only, not the surface the button sits in.
        event.stopPropagation();
        closeAndFocus();
        return;
      case 'Tab':
        // Back on the button before the browser moves focus, so Tab continues from the filter bar
        // rather than from the end of the page where the list is portalled.
        if (open) closeAndFocus();
        return;
      default:
    }
  };

  return (
    <div className={cn('relative', wrapperClassName)}>
      <button
        ref={triggerRef}
        id={triggerId}
        type="button"
        role="combobox"
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={open ? listId : undefined}
        aria-activedescendant={open && !searchPlaceholder && activeIndex >= 0 ? `${listId}-${activeIndex}` : undefined}
        aria-label={ariaLabel}
        disabled={disabled}
        onClick={() => (open ? closeAndFocus() : openList())}
        onKeyDown={handleKeyDown}
        className={selectTriggerVariants()}
      >
        <span className={cn('truncate', triggerText === null && 'text-muted-foreground')}>{triggerText ?? placeholder}</span>
        <ChevronDown
          aria-hidden="true"
          className={cn('h-4 w-4 shrink-0 text-muted-foreground transition-transform duration-150', open && 'rotate-180')}
        />
      </button>

      {open && position && createPortal(
        <div
          ref={panelRef}
          // Marks a surface portalled out of its logical parent, for dismiss handlers that test for it.
          data-portal-surface=""
          style={{
            position: 'fixed',
            left: position.left,
            top: position.top,
            bottom: position.bottom,
            minWidth: position.minWidth,
            maxWidth: position.maxWidth,
            width: 'max-content',
            maxHeight: position.maxHeight,
          }}
          className="z-50 flex flex-col rounded-lg border border-border bg-popover p-1 text-popover-foreground shadow-xl ring-1 ring-black/5 animate-popover-in"
        >
          {searchPlaceholder && (
            <div className="mb-1 flex h-8 shrink-0 items-center gap-2 border-b border-border px-2">
              <Search aria-hidden="true" className="h-4 w-4 shrink-0 text-muted-foreground" />
              <Input
                type="search"
                // The list was opened to pick from it, so typing goes straight to the search.
                autoFocus
                size={1}
                value={query}
                onChange={event => {
                  setQuery(event.target.value);
                  setActiveIndex(0);
                }}
                onKeyDown={handleKeyDown}
                placeholder={searchPlaceholder}
                aria-label={searchPlaceholder}
                aria-controls={listId}
                aria-activedescendant={visible[activeIndex] ? `${listId}-${activeIndex}` : undefined}
                className="h-full min-w-0 flex-1 rounded-none border-0 bg-transparent px-0 py-0 shadow-none focus-visible:ring-0 focus-visible:ring-offset-0 [&::-webkit-search-cancel-button]:appearance-none"
              />
            </div>
          )}
          <ul
            id={listId}
            role="listbox"
            aria-multiselectable="true"
            aria-labelledby={triggerId}
            className="min-h-0 flex-1 overflow-y-auto overscroll-contain"
          >
            {visible.map((option, index) => {
              const checked = chosen.has(option.value);
              return (
                <li
                  key={option.value}
                  id={`${listId}-${index}`}
                  role="option"
                  aria-selected={checked}
                  title={option.label}
                  // Keeps focus, and with it the keyboard contract, on the button or the search row.
                  onMouseDown={event => event.preventDefault()}
                  onMouseEnter={() => setActiveIndex(index)}
                  onClick={() => toggle(option.value)}
                  className={cn(
                    'flex cursor-pointer select-none items-center gap-3 rounded-md px-2 py-1.5 text-sm',
                    index === activeIndex && 'bg-accent text-accent-foreground',
                  )}
                >
                  <CheckboxIndicator checked={checked} />
                  <span className="flex min-w-0 items-center gap-2">
                    {option.dotClassName && <span aria-hidden="true" className={cn('h-2 w-2 shrink-0 rounded-full', option.dotClassName)} />}
                    <span className="truncate">{option.label}</span>
                  </span>
                </li>
              );
            })}
            {visible.length === 0 && <li className="px-2 py-1.5 text-sm text-muted-foreground">No matches</li>}
          </ul>
          <div className="mt-1 flex shrink-0 items-center justify-between gap-4 border-t border-border px-2 pb-1 pt-2 text-xs text-muted-foreground">
            <span>{`${values.length} of ${options.length} chosen`}</span>
            <Button variant="link" onClick={() => onValuesChange([])} disabled={values.length === 0}>Clear</Button>
          </div>
        </div>,
        document.body,
      )}
    </div>
  );
}
