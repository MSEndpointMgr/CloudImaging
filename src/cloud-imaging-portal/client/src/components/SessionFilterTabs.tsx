import { cn } from '../lib/utils.ts';

interface FilterTabsProps {
  activeFilter: string;
  onFilterChange: (filter: string) => void;
  counts: { active: number; completed: number; failed: number; all: number };
}

const TABS = [
  { key: 'active',    label: 'Active' },
  { key: 'completed', label: 'Completed' },
  { key: 'failed',    label: 'Failed' },
  { key: 'all',       label: 'All' },
] as const;

/**
 * Session filter tabs with real-time count badges (T042, FR-031).
 * Styled as a shadcn segmented control.
 */
export function SessionFilterTabs({ activeFilter, onFilterChange, counts }: FilterTabsProps): React.ReactElement {
  return (
    <div className="inline-flex items-center gap-1 rounded-lg bg-muted p-1">
      {TABS.map((tab) => {
        const count = counts[tab.key] ?? 0;
        const isActive = tab.key === activeFilter;
        return (
          <button
            key={tab.key}
            onClick={() => onFilterChange(tab.key)}
            className={cn(
              'inline-flex items-center gap-2 rounded-md px-3 py-2 text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
              isActive
                ? 'bg-background text-foreground shadow-sm'
                : 'text-muted-foreground hover:text-foreground',
            )}
          >
            {tab.label}
            <span
              className={cn(
                'inline-flex min-w-5 items-center justify-center rounded-full px-2 py-0.5 text-xs font-semibold',
                isActive ? 'bg-primary text-primary-foreground' : 'bg-background/70 text-muted-foreground',
              )}
            >
              {count}
            </span>
          </button>
        );
      })}
    </div>
  );
}
