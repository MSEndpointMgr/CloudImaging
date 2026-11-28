import * as React from 'react';
import { MoreHorizontal } from 'lucide-react';
import { Button } from './button.tsx';
import { Tooltip } from './tooltip.tsx';
import { cn } from '../../lib/utils';

/** One entry of a {@link MoreMenu}: an icon and a one-line label, like every other entry. */
export interface MoreMenuItem {
  label: string;
  icon: React.ComponentType<{ className?: string; 'aria-hidden'?: boolean | 'true' }>;
  onSelect: () => void;
  /** Disables the entry; shown in its tooltip, as a disabled button explains itself. */
  disabledReason?: string;
  /** Removes or deletes something; drawn in the destructive colour. */
  destructive?: boolean;
}

/**
 * The "More actions" overflow menu: an outline icon button that opens a list of actions. Arrow
 * keys move between entries, Escape and a click outside close it, and focus returns to the
 * button. Use it for actions people need now and then, so the header keeps one visible action.
 */
export function MoreMenu({ items, label = 'More actions', className }: { items: MoreMenuItem[]; label?: string; className?: string }): React.ReactElement {
  const [open, setOpen] = React.useState(false);
  const trigger = React.useRef<HTMLButtonElement>(null);
  const menu = React.useRef<HTMLDivElement>(null);
  const menuId = React.useId();

  const close = React.useCallback((refocus: boolean) => {
    setOpen(false);
    if (refocus) trigger.current?.focus();
  }, []);

  // The first enabled entry takes focus when the menu opens, as a menu button does.
  React.useEffect(() => {
    if (!open) return undefined;
    menu.current?.querySelector<HTMLButtonElement>('[role="menuitem"]:not([aria-disabled="true"])')?.focus();
    const outside = (event: MouseEvent) => {
      if (!menu.current?.contains(event.target as Node) && !trigger.current?.contains(event.target as Node)) close(false);
    };
    document.addEventListener('mousedown', outside);
    return () => document.removeEventListener('mousedown', outside);
  }, [open, close]);

  const onKeyDown = (event: React.KeyboardEvent<HTMLDivElement>) => {
    // Disabled entries stay reachable, so their tooltip can say why.
    const entries = [...(menu.current?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]') ?? [])];
    const index = entries.indexOf(document.activeElement as HTMLButtonElement);
    if (event.key === 'Escape' || event.key === 'Tab') {
      event.preventDefault();
      close(true);
    } else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      const step = event.key === 'ArrowDown' ? 1 : -1;
      entries[(index + step + entries.length) % entries.length]?.focus();
    } else if (event.key === 'Home' || event.key === 'End') {
      event.preventDefault();
      entries[event.key === 'Home' ? 0 : entries.length - 1]?.focus();
    }
  };

  return (
    <div className={cn('relative', className)}>
      <Tooltip content={label}>
        <Button
          ref={trigger}
          variant="outline"
          size="icon"
          aria-label={label}
          aria-haspopup="menu"
          aria-expanded={open}
          aria-controls={open ? menuId : undefined}
          onClick={() => setOpen(o => !o)}
        >
          <MoreHorizontal />
        </Button>
      </Tooltip>
      {open && (
        <div
          ref={menu}
          id={menuId}
          role="menu"
          aria-label={label}
          onKeyDown={onKeyDown}
          className="absolute right-0 top-10 z-50 w-max min-w-32 rounded-lg border border-border bg-popover p-1 text-popover-foreground shadow-lg"
        >
          {items.map(item => {
            const Icon = item.icon;
            const disabled = item.disabledReason !== undefined;
            const entry = (
              <button
                key={item.label}
                type="button"
                role="menuitem"
                aria-disabled={disabled || undefined}
                onClick={() => { if (disabled) return; close(true); item.onSelect(); }}
                className={cn(
                  'flex w-full items-center gap-2 whitespace-nowrap rounded-md px-2 py-2 text-left text-sm hover:bg-accent focus-visible:bg-accent focus-visible:outline-none',
                  'aria-disabled:cursor-default aria-disabled:opacity-50 aria-disabled:hover:bg-transparent',
                  item.destructive && 'text-destructive',
                )}
              >
                <Icon className="h-4 w-4 shrink-0" aria-hidden="true" />
                {item.label}
              </button>
            );
            return disabled ? <Tooltip key={item.label} content={item.disabledReason} className="flex w-full">{entry}</Tooltip> : entry;
          })}
        </div>
      )}
    </div>
  );
}
