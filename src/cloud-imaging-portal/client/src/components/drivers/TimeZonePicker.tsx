import React, { useEffect, useId, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Check, ChevronDown, Search } from 'lucide-react';
import { Input } from '../ui/input.tsx';
import { cn } from '../../lib/utils.ts';
import type { DriverTimeZone } from '../../lib/drivers.ts';

/**
 * Searchable time zone picker: the Windows display names in the Windows order, with the browser's
 * entry first under "Your browser". The search box keeps focus; arrows move through the matches
 * and Enter picks one.
 */
export function TimeZonePicker({ zones, value, browser, disabled, onChange }: {
  zones: DriverTimeZone[];
  value: string;
  /** The entry whose `covers` includes the browser's zone. */
  browser?: DriverTimeZone;
  disabled?: boolean;
  onChange: (ianaId: string) => void;
}): React.ReactElement {
  const id = useId();
  const triggerRef = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useState(false);
  const [search, setSearch] = useState('');
  const [active, setActive] = useState(0);
  const [position, setPosition] = useState<{ left: number; top: number; width: number }>({ left: 0, top: 0, width: 440 });
  const current = zones.find(z => z.ianaId === value);

  const groups = useMemo(() => {
    const term = search.trim().toLowerCase();
    const matches = (z: DriverTimeZone) => !term || z.displayName.toLowerCase().includes(term) || z.covers.some(c => c.toLowerCase().includes(term));
    const all = zones.filter(matches);
    return [
      { label: 'Your browser', items: browser && matches(browser) ? [browser] : [] },
      { label: 'All time zones', items: all },
    ].filter(g => g.items.length > 0);
  }, [zones, browser, search]);
  const flat = groups.flatMap(g => g.items);

  useEffect(() => {
    if (!open) return;
    const rect = triggerRef.current?.getBoundingClientRect();
    if (rect) setPosition({ left: rect.left, top: rect.bottom + 4, width: rect.width });
    const close = (event: MouseEvent) => {
      const target = event.target as Node;
      if (!document.getElementById(`${id}-popover`)?.contains(target) && !triggerRef.current?.contains(target)) setOpen(false);
    };
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, [open, id]);

  const choose = (zone: DriverTimeZone) => {
    setOpen(false);
    setSearch('');
    triggerRef.current?.focus();
    if (zone.ianaId !== value) onChange(zone.ianaId);
  };

  const onKey = (event: React.KeyboardEvent) => {
    if (event.key === 'Escape') {
      event.preventDefault();
      setOpen(false);
      triggerRef.current?.focus();
    } else if (event.key === 'ArrowDown') {
      event.preventDefault();
      setActive(a => Math.min(a + 1, flat.length - 1));
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      setActive(a => Math.max(a - 1, 0));
    } else if (event.key === 'Enter' && flat[active]) {
      event.preventDefault();
      choose(flat[active]);
    }
  };

  let index = -1;
  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        role="combobox"
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={open ? `${id}-list` : undefined}
        aria-label={`Time zone: ${current?.displayName ?? value}`}
        disabled={disabled}
        onClick={() => { setActive(0); setOpen(o => !o); }}
        className="flex h-8 w-[440px] items-center justify-between gap-2 rounded-md border border-input bg-background pl-3 pr-2 text-left text-sm shadow-sm hover:bg-accent/40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
      >
        <span className="truncate">{current?.displayName ?? value}</span>
        <ChevronDown className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden="true" />
      </button>
      {open && createPortal(
        <div
          id={`${id}-popover`}
          className="fixed z-50 space-y-2 rounded-md border border-border bg-popover p-2 text-popover-foreground shadow-xl"
          style={{ left: position.left, top: position.top, width: Math.max(position.width, 440) }}
        >
          <div className="relative flex items-center">
            <Search aria-hidden="true" className="pointer-events-none absolute left-3 h-4 w-4 text-muted-foreground" />
            <Input
              type="search"
              value={search}
              onChange={e => { setSearch(e.target.value); setActive(0); }}
              placeholder="Search time zones"
              aria-label="Search time zones"
              aria-controls={`${id}-list`}
              aria-activedescendant={flat[active] ? `${id}-option-${active}` : undefined}
              onKeyDown={onKey}
              autoFocus
              className="w-full pl-9"
            />
          </div>
          <div id={`${id}-list`} role="listbox" aria-label="Time zones" className="max-h-72 overflow-y-auto">
            {groups.map(group => (
              <div key={group.label} role="group" aria-label={group.label}>
                <div className="px-2 py-1 text-xs font-semibold text-muted-foreground" aria-hidden="true">{group.label}</div>
                {group.items.map(zone => {
                  index += 1;
                  const optionIndex = index;
                  return (
                    <div
                      key={`${group.label}-${zone.ianaId}`}
                      id={`${id}-option-${optionIndex}`}
                      role="option"
                      aria-selected={zone.ianaId === value}
                      onMouseDown={e => e.preventDefault()}
                      onMouseEnter={() => setActive(optionIndex)}
                      onClick={() => choose(zone)}
                      className={cn('flex cursor-pointer items-center gap-2 rounded-md px-2 py-1 text-sm', optionIndex === active && 'bg-accent text-accent-foreground')}
                    >
                      <span className="min-w-0 flex-1 truncate">{zone.displayName}</span>
                      {zone.ianaId === value && <Check className="h-4 w-4 shrink-0" aria-hidden="true" />}
                    </div>
                  );
                })}
              </div>
            ))}
            {flat.length === 0 && <p className="px-2 py-1 text-sm text-muted-foreground">No time zone matches.</p>}
          </div>
        </div>,
        document.body,
      )}
    </>
  );
}
