import * as React from 'react';
import { createPortal } from 'react-dom';
import { type VariantProps } from 'class-variance-authority';
import { Check, ChevronDown } from 'lucide-react';
import { cn } from '../../lib/utils.ts';
import { useAnchoredPosition } from './anchored-position.ts';
import { selectTriggerVariants } from './select-trigger.ts';

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
  /**
   * Heading of the group this option belongs to. A heading row is drawn where the group changes;
   * it is not selectable, so arrow keys and type-ahead skip it.
   */
  group?: string;
  /** Right-aligned muted text, for example a size. Not matched by type-ahead. */
  meta?: string;
}

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
  /** Size the closed trigger to the widest option, including before a value is selected. */
  sizeToOptions?: boolean;
  /**
   * Applied to the positioning wrapper instead of the trigger. The wrapper is what participates
   * in the parent's flex/grid layout, so width constraints belong there.
   */
  wrapperClassName?: string;
}

const TYPEAHEAD_RESET_MS = 500;
/** 18rem. Roughly seven rows: enough to browse without the list dominating the viewport. */
const MAX_LIST_HEIGHT = 288;

/** One row of the list: label, optional description and meta, and the check mark when selected. */
function SelectOptionRow({ option, index, listId, selected, active, onHover, onChoose }: {
  option: SelectOption;
  index: number;
  listId: string;
  selected: boolean;
  active: boolean;
  onHover: (index: number) => void;
  onChoose: (index: number) => void;
}): React.ReactElement {
  return (
    <li
      id={`${listId}-${index}`}
      role="option"
      aria-selected={selected}
      aria-disabled={option.disabled || undefined}
      title={option.label}
      // Keeps focus (and therefore the keyboard contract) on the trigger while the
      // pointer interacts with the list.
      onMouseDown={e => e.preventDefault()}
      onMouseEnter={() => { if (!option.disabled) onHover(index); }}
      onClick={() => onChoose(index)}
      className={cn(
        'relative flex cursor-pointer select-none items-center rounded-md px-2 py-1.5 text-sm',
        active && !option.disabled && 'bg-accent text-accent-foreground',
        selected && 'font-medium',
        option.disabled && 'cursor-not-allowed opacity-50',
        option.value === '' && 'text-muted-foreground',
      )}
    >
      <span className={cn('min-w-0 flex-1', selected && !option.meta && 'pr-6')}>
        <span className="block truncate">{option.label}</span>
        {option.description && (
          <span className="block truncate text-xs text-muted-foreground">
            {option.description}
          </span>
        )}
      </span>
      {option.meta && (
        <span className={cn('ml-2 shrink-0 text-xs text-muted-foreground', selected && 'mr-6')}>{option.meta}</span>
      )}
      {selected && (
        <Check
          aria-hidden="true"
          data-select-check=""
          className="absolute right-2 h-4 w-4 text-primary"
        />
      )}
    </li>
  );
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
 * flips above the trigger when there is more room there. It opens exactly as wide as the trigger
 * and only grows for an option that needs the room, so a long name is read rather than cut off.
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
  sizeToOptions = false,
  wrapperClassName,
  size,
}: SelectProps): React.ReactElement {
  const generatedId = React.useId();
  const triggerId = id ?? `${generatedId}-trigger`;
  const listId = `${generatedId}-list`;

  const [open, setOpen] = React.useState(false);
  const [activeIndex, setActiveIndex] = React.useState(-1);

  const triggerRef = React.useRef<HTMLButtonElement>(null);
  const listRef = React.useRef<HTMLUListElement>(null);
  const typeahead = React.useRef({ buffer: '', at: 0 });
  const position = useAnchoredPosition(triggerRef, open, { preferredHeight: MAX_LIST_HEIGHT, panelRef: listRef });

  const items = React.useMemo<SelectOption[]>(
    () => (allowEmpty ? [{ value: '', label: placeholder }, ...options] : options),
    [allowEmpty, options, placeholder],
  );

  const selectedIndex = items.findIndex(o => o.value === value);
  const selectedLabel = selectedIndex >= 0 ? items[selectedIndex].label : undefined;

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
    document.getElementById(`${listId}-${activeIndex}`)?.scrollIntoView?.({ block: 'nearest' });
  }, [open, activeIndex, listId]);

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

  // Consecutive options of one group are drawn under its heading; options without a group are
  // drawn as they always were.
  const sections = React.useMemo(() => {
    const result: { group?: string; entries: { option: SelectOption; index: number }[] }[] = [];
    items.forEach((option, index) => {
      const last = result[result.length - 1];
      if (last && last.group === option.group) last.entries.push({ option, index });
      else result.push({ group: option.group, entries: [{ option, index }] });
    });
    return result;
  }, [items]);



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
    <div
      className={cn(
        'relative',
        sizeToOptions && 'inline-grid grid-cols-[minmax(0,1fr)]',
        wrapperClassName,
      )}
    >
      {sizeToOptions && (
        <div
          aria-hidden="true"
          data-select-width-sizer=""
          className={cn(
            selectTriggerVariants({ size }),
            'invisible pointer-events-none col-start-1 row-start-1 h-0 w-max border-0 shadow-none',
          )}
        >
          <span>
            <span className="block whitespace-nowrap">{placeholder}</span>
            {items.map(option => (
              <span key={option.value || '__empty__'} className="block whitespace-nowrap">
                {option.label}
              </span>
            ))}
          </span>
          <span className="h-4 w-4 shrink-0" />
        </div>
      )}
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
        className={cn(
          selectTriggerVariants({ size }),
          sizeToOptions && 'col-start-1 row-start-1',
          className,
        )}
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
              minWidth: position.minWidth,
              maxWidth: position.maxWidth,
              width: 'max-content',
              maxHeight: position.maxHeight,
            }}
            className={cn(
              'z-50 overflow-y-auto overscroll-contain rounded-lg border border-border bg-popover p-1',
              'text-popover-foreground shadow-xl ring-1 ring-black/5 animate-popover-in',
            )}
          >
            {sections.map((section, sectionIndex) => section.group === undefined
              ? section.entries.map(({ option, index }) => (
                <SelectOptionRow key={option.value || '__empty__'} option={option} index={index} listId={listId} selected={option.value === value} active={index === activeIndex} onHover={setActiveIndex} onChoose={commit} />
              ))
              : (
                <li key={`group-${sectionIndex}`} role="presentation">
                  <div aria-hidden="true" className="px-2 pb-1 pt-2 text-xs font-semibold text-muted-foreground">{section.group}</div>
                  <ul role="group" aria-label={section.group}>
                    {section.entries.map(({ option, index }) => (
                      <SelectOptionRow key={option.value || '__empty__'} option={option} index={index} listId={listId} selected={option.value === value} active={index === activeIndex} onHover={setActiveIndex} onChoose={commit} />
                    ))}
                  </ul>
                </li>
              ))}
            {items.length === 0 && (
              <li className="px-2 py-1.5 text-sm text-muted-foreground">No options available</li>
            )}
          </ul>,
          document.body,
        )}
    </div>
  );
}
