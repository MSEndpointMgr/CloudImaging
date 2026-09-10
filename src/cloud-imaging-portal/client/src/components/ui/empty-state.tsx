import * as React from 'react';
import type { LucideIcon } from 'lucide-react';
import { cn } from '../../lib/utils';

interface EmptyStateProps {
  /** Icon shown inside a softly-circled badge above the title. */
  icon: LucideIcon;
  title: string;
  /** Optional supporting copy (plain text or nodes, e.g. an inline `<Link>`). */
  description?: React.ReactNode;
  /**
   * Optional call to action. An empty state is the one moment the user is guaranteed to be
   * looking at a screen with nothing else on it, so it is the cheapest place to offer the action
   * that would fill it - otherwise it just reports a dead end and leaves them to find the button.
   */
  action?: React.ReactNode;
  className?: string;
}

/**
 * Modern empty-state placeholder for table bodies — a muted icon badge, a short title, and an
 * optional description — used in place of a bare "No X found." string spanning the table.
 */
export function EmptyState({ icon: Icon, title, description, action, className }: EmptyStateProps): React.ReactElement {
  return (
    <div className={cn('flex select-none flex-col items-center justify-center gap-2 py-12 text-center', className)}>
      <div className="flex h-11 w-11 items-center justify-center rounded-full bg-muted">
        <Icon className="h-5 w-5 text-muted-foreground" aria-hidden="true" />
      </div>
      <p className="text-sm font-medium text-foreground">{title}</p>
      {description && <p className="max-w-sm text-sm text-muted-foreground">{description}</p>}
      {action && <div className="mt-2">{action}</div>}
    </div>
  );
}
