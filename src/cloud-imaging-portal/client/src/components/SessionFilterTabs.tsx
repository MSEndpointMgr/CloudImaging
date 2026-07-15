import { useState, useCallback } from 'react';

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
 */
export function SessionFilterTabs({ activeFilter, onFilterChange, counts }: FilterTabsProps): React.ReactElement {
  return (
    <div className="flex gap-1 border-b border-border">
      {TABS.map((tab) => {
        const count = counts[tab.key as keyof typeof counts] ?? 0;
        const isActive = tab.key === activeFilter;
        return (
          <button
            key={tab.key}
            onClick={() => onFilterChange(tab.key)}
            className={[
              'flex items-center gap-1.5 px-4 py-2 text-sm font-medium border-b-2 transition-colors',
              isActive
                ? 'border-primary text-primary'
                : 'border-transparent text-muted-foreground hover:text-foreground',
            ].join(' ')}
          >
            {tab.label}
            <span className={[
              'inline-flex items-center justify-center rounded-full px-1.5 py-0.5 text-xs font-semibold',
              isActive ? 'bg-primary text-primary-foreground' : 'bg-muted text-muted-foreground',
            ].join(' ')}>
              {count}
            </span>
          </button>
        );
      })}
    </div>
  );
}
