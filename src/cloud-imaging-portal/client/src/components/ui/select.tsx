import * as React from 'react';
import { createPortal } from 'react-dom';
import { cva, type VariantProps } from 'class-variance-authority';
import { Check, ChevronDown } from 'lucide-react';
import { cn } from '../../lib/utils.ts';

/**
 * A single choice in a {@link Select}.
 *
 * `label` is what the user reads and what type-ahead matches against, so it is a plain string
 * rather than a node.
 */
export interface SelectOption {
  value: string;
  label: string;
  /** Secondary line under the label. Not matched by type-ahead. */
  description?: string;
  disabled?: boolean;
}

/**
 * Trigger geometry deliberately mirrors `Button`'s size vocabulary (`sm` = h-8, `default` = h-9)
 * rather than inventing a third scale, because selects are almost always laid out beside a button
 * (the OS image picker sits next to "Start Imaging") and a 4px height difference between two
 * adjacent controls reads as a rendering bug.
 */
const selectTriggerVariants = cva(
  cn(
    'flex w-full items-center justify-between gap-2 rounded-md border border-input bg-background text-sm shadow-sm',
    'transition-colors hover:bg-accent/40',
    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background',
    'disabled:cursor-not-allowed disabled:opacity-50 disabled:hover:bg-background',
  ),
  {
    variants: {
      size: {
        default: 'h-9 pl-3 pr-2.5',
        sm: 'h-8 pl-2.5 pr-2',
      },
    },
    defaultVariants: { size: 'default' },
  },
);

export interface SelectProps extends VariantProps<typeof selectTriggerVariants> {
  /** Currently selected option value. An empty string means nothing is selected. */
  value: string;
  onValueChange: (value: string) => void;
  options: SelectOption[];
  /** Shown on the trigger while `value` is empty, and as the label of the clear-selection row. */
  placeholder?: string;
  /**
   * When set, an option with an empty value is rendered first so the user can return to the
   * unselected state. Off by default: most pickers require a choice once one has been made.
   */
  allowEmpty?: boolean;
  id?: string;
  disabled?: boolean;
  title?: string;
  'aria-label'?: string;
  /** Applied to the trigger button. */
  className?: string;
  /**
   * Applied to the positioning wrapper instead of the trigger. The wrapper is what participates
   * in the parent's flex/grid layout, so width constraints belong there.
   */
  wrapperClassName?: string;
}

const TYPEAHEAD_RESET_MS = 500;
/** 18rem. Roughly seven rows: enough to browse without the list dominating the viewport. */
const MAX_LIST_HEIGHT = 288;
/** Gap between the trigger and the list, and the minimum breathing room at the viewport edge. */
const VIEWPORT_MARGIN = 8;

interface ListPosition {
  left: number;
  width: number;
  maxHeight: number;
  /** Exactly one of `top`/`bottom` is set, depending on the side the list flipped to. */
  top?: number;
  bottom?: number;
}

/**
 * Themed single-choice picker.
 *
 * Replaces the native `<select>`. A native option list is drawn by the browser, not by the page:
 * it ignores every design token (surface, radius, brand accent, font), it can only be nudged
 * towards the dark theme with `color-scheme`, and it sizes itself to its longest option regardless
 * of the control's width, so long catalog names ran off the side of the page. Rendering the list
 * here fixes all three and lets the selected row carry a check mark.
 *
 * The list is portalled to `document.body` and positioned from the trigger's viewport rect. That
 * is not cosmetic: several call sites sit inside `overflow-hidden` card headers, which would clip
 * an in-flow absolutely positioned list. Position is recomputed on scroll and resize, and the list
 * flips above the trigger when there is more room there.
 *
 * Keyboard and screen-reader behaviour follows the ARIA combobox pattern, with focus kept on the
 * trigger and the active row tracked through `aria-activedescendant`: arrows, Home/End, Enter,
 * Space, Escape, Tab, and printable-character type-ahead all behave as they did natively.
 */
export function Select({
  value,
  onValueChange,
  options,
  placeholder = 'Select\u2026',
  allowEmpty = false,
  id,
  disabled = false,
  title,
  'aria-label': ariaLabel,
  className,
  wrapperClassName,
  size,
}: SelectProps): React.ReactElement {
  const generatedId = React.useId();
  const triggerId = id ?? `${generatedId}-trigger`;
  const listId = `${generatedId}-list`;

  const [open, setOpen] = React.useState(false);
  const [activeIndex, setActiveIndex] = React.useState(-1);
  const [position, setPosition] = React.useState<ListPosition | null>(null);

  const triggerRef = React.useRef<HTMLButtonElement>(null);
  const listRef = React.useRef<HTMLUListElement>(null);
  const typeahead = React.useRef({ buffer: '', at: 0 });

  const items = React.useMemo<SelectOption[]>(
    () => (allowEmpty ? [{ value: '', label: placeholder }, ...options] : options),
    [allowEmpty, options, placeholder],
  );

  const selectedIndex = items.findIndex(o => o.value === value);
  const selectedLabel = selectedIndex >= 0 ? items[selectedIndex].label : undefined;

  const updatePosition = React.useCallback(() => {
    const trigger = triggerRef.current;
    if (!trigger) return;

    const rect = trigger.getBoundingClientRect();
    const spaceBelow = window.innerHeight - rect.bottom - VIEWPORT_MARGIN * 2;
    const spaceAbove = rect.top - VIEWPORT_MARGIN * 2;
    // Below unless it genuinely does not fit and there is more room above, so the list does not
    // jump sides for a trigger that is merely close to the fold.
    const placeBelow = spaceBelow >= MAX_LIST_HEIGHT || spaceBelow >= spaceAbove;

    const width = rect.width;
    const maxLeft = window.innerWidth - width - VIEWPORT_MARGIN;
    const left = Math.max(VIEWPORT_MARGIN, Math.min(rect.left, maxLeft));

    setPosition({
      left,
      width,
      maxHeight: Math.max(120, Math.min(MAX_LIST_HEIGHT, placeBelow ? spaceBelow : spaceAbove)),
      ...(placeBelow
        ? { top: rect.bottom + VIEWPORT_MARGIN }
        : { bottom: window.innerHeight - rect.top + VIEWPORT_MARGIN }),
    });
  }, []);

  React.useLayoutEffect(() => {
    if (!open) {
      setPosition(null);
      return;
    }
    updatePosition();

    // Capture phase so the list also tracks scrolling of any nested scroll container the trigger
    // sits in, not just the window.
    const onReflow = (): void => updatePosition();
    window.addEventListener('scroll', onReflow, true);
    window.addEventListener('resize', onReflow);
    return () => {
      window.removeEventListener('scroll', onReflow, true);
      window.removeEventListener('resize', onReflow);
    };
  }, [open, updatePosition]);

  React.useEffect(() => {
    if (!open) return;
    const onPointerDown = (e: MouseEvent): void => {
      const target = e.target as Node;
      if (triggerRef.current?.contains(target) || listRef.current?.contains(target)) return;
      setOpen(false);
      setActiveIndex(-1);
    };
    document.addEventListener('mousedown', onPointerDown);
    return () => document.removeEventListener('mousedown', onPointerDown);
  }, [open]);

  React.useEffect(() => {
    if (!open || activeIndex < 0) return;
    listRef.current?.children[activeIndex]?.scrollIntoView({ block: 'nearest' });
  }, [open, activeIndex]);

  /** Next selectable index in the `step` direction, skipping disabled rows, stopping at the ends. */
  const nextEnabled = (from: number, step: number): number => {
    for (let i = from; i >= 0 && i < items.length; i += step) {
      if (!items[i].disabled) return i;
    }
    return activeIndex;
  };

  const openList = (index: number): void => {
    if (disabled) return;
    setActiveIndex(index);
    setOpen(true);
  };

  const closeList = (): void => {
    setOpen(false);
    setActiveIndex(-1);
  };

  const commit = (index: number): void => {
    const option = items[index];
    if (!option || option.disabled) return;
    if (option.value !== value) onValueChange(option.value);
    closeList();
    triggerRef.current?.focus();
  };

  const initialIndex = (): number => nextEnabled(selectedIndex >= 0 ? selectedIndex : 0, 1);

  const handleKeyDown = (e: React.KeyboardEvent<HTMLButtonElement>): void => {
    if (disabled || items.length === 0) return;

    switch (e.key) {
      case 'ArrowDown':
      case 'ArrowUp': {
        e.preventDefault();
        const step = e.key === 'ArrowDown' ? 1 : -1;
        if (open) setActiveIndex(nextEnabled(activeIndex + step, step));
        else openList(initialIndex());
        return;
      }
      case 'Home':
      case 'End': {
        if (!open) return;
        e.preventDefault();
        setActiveIndex(e.key === 'Home' ? nextEnabled(0, 1) : nextEnabled(items.length - 1, -1));
        return;
      }
      case 'Enter':
      case ' ': {
        e.preventDefault();
        if (open) commit(activeIndex);
        else openList(initialIndex());
        return;
      }
      case 'Escape': {
        if (!open) return;
        e.preventDefault();
        // Escape closes the list only. Without this the same keypress would also reach the
        // document-level dismiss handler of whatever surface the trigger lives in (the account
        // menu, a dialog) and tear the whole thing down under the user.
        e.stopPropagation();
        closeList();
        return;
      }
      case 'Tab': {
        // Deliberately not prevented: dismiss the list but let focus move on.
        if (open) closeList();
        return;
      }
      default:
        break;
    }

    // Type-ahead. Single printable characters only, so modifier chords still reach the browser.
    if (e.key.length !== 1 || e.altKey || e.ctrlKey || e.metaKey) return;
    const now = Date.now();
    typeahead.current.buffer =
      now - typeahead.current.at > TYPEAHEAD_RESET_MS ? e.key : typeahead.current.buffer + e.key;
    typeahead.current.at = now;

    const query = typeahead.current.buffer.toLowerCase();
    const match = items.findIndex(o => !o.disabled && o.label.toLowerCase().startsWith(query));
    if (match < 0) return;
    e.preventDefault();
    if (open) setActiveIndex(match);
    else commit(match);
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
        aria-activedescendant={open && activeIndex >= 0 ? `${listId}-${activeIndex}` : undefined}
        aria-label={ariaLabel}
        disabled={disabled}
        title={title}
        onClick={() => (open ? closeList() : openList(initialIndex()))}
        onKeyDown={handleKeyDown}
        className={cn(selectTriggerVariants({ size }), className)}
      >
        <span className={cn('truncate', selectedLabel === undefined && 'text-muted-foreground')}>
          {selectedLabel ?? placeholder}
        </span>
        <ChevronDown
          aria-hidden="true"
          className={cn(
            'h-4 w-4 shrink-0 text-muted-foreground transition-transform duration-150',
            open && 'rotate-180',
          )}
        />
      </button>

      {open &&
        position &&
        createPortal(
          <ul
            ref={listRef}
            id={listId}
            role="listbox"
            aria-labelledby={triggerId}
            // Marks this as a surface that is portalled out of its logical parent. Dismiss-on-
            // outside-click handlers (the account menu's, for one) test for it, because a DOM
            // containment check against their own subtree cannot see a node under <body>.
            data-portal-surface=""
            style={{
              position: 'fixed',
              left: position.left,
              top: position.top,
              bottom: position.bottom,
              width: position.width,
              maxHeight: position.maxHeight,
            }}
            className={cn(
              'z-50 overflow-y-auto overscroll-contain rounded-lg border border-border bg-popover p-1',
              'text-popover-foreground shadow-xl ring-1 ring-black/5 animate-popover-in',
            )}
          >
            {items.map((option, index) => {
              const isSelected = option.value === value;
              return (
                <li
                  key={option.value || '__empty__'}
                  id={`${listId}-${index}`}
                  role="option"
                  aria-selected={isSelected}
                  aria-disabled={option.disabled || undefined}
                  title={option.label}
                  // Keeps focus (and therefore the keyboard contract) on the trigger while the
                  // pointer interacts with the list.
                  onMouseDown={e => e.preventDefault()}
                  onMouseEnter={() => !option.disabled && setActiveIndex(index)}
                  onClick={() => commit(index)}
                  className={cn(
                    'flex cursor-pointer select-none items-center gap-2 rounded-md px-2 py-1.5 text-sm',
                    index === activeIndex && !option.disabled && 'bg-accent text-accent-foreground',
                    isSelected && 'font-medium',
                    option.disabled && 'cursor-not-allowed opacity-50',
                    option.value === '' && 'text-muted-foreground',
                  )}
                >
                  <Check
                    aria-hidden="true"
                    className={cn('h-4 w-4 shrink-0 text-primary', !isSelected && 'invisible')}
                  />
                  <span className="min-w-0 flex-1">
                    <span className="block truncate">{option.label}</span>
                    {option.description && (
                      <span className="block truncate text-xs text-muted-foreground">
                        {option.description}
                      </span>
                    )}
                  </span>
                </li>
              );
            })}
            {items.length === 0 && (
              <li className="px-2 py-1.5 text-sm text-muted-foreground">No options available</li>
            )}
          </ul>,
          document.body,
        )}
    </div>
  );
}
