import { cn } from '../../lib/utils.ts';

/**
 * The card the Sync tab is built from: a header with the title and description (and optionally
 * controls beside them), then rows or a table.
 */
export function DriverCard({ title, description, aside, divided = true, children }: {
  title: string;
  description: React.ReactNode;
  /** Controls shown to the right of the title and description. */
  aside?: React.ReactNode;
  /** Draws the line under the header; off when the first row draws its own. */
  divided?: boolean;
  children?: React.ReactNode;
}): React.ReactElement {
  return (
    <section className="rounded-xl border border-border bg-card text-card-foreground shadow-sm">
      <div className={cn('flex items-center justify-between gap-6 px-5 py-4', divided && 'border-b border-border')}>
        <div className="min-w-0 space-y-1">
          <h3 className="text-base font-semibold">{title}</h3>
          <div className="max-w-[72ch] text-sm text-muted-foreground">{description}</div>
        </div>
        {aside && <div className="shrink-0">{aside}</div>}
      </div>
      {children}
    </section>
  );
}

/** Rows inside a DriverCard, separated by a line. */
export function DriverCardRows({ className, children }: { className?: string; children: React.ReactNode }): React.ReactElement {
  return <div className={cn('divide-y divide-border/70 px-5', className)}>{children}</div>;
}

/** One setting: label and help on the left, a 240px control column on the right. */
export function DriverCardRow({ label, help, mutedLabel = false, children }: {
  label: string;
  help: React.ReactNode;
  mutedLabel?: boolean;
  children: React.ReactNode;
}): React.ReactElement {
  return (
    <div className="flex items-center justify-between gap-6 py-4">
      <div className="min-w-0 space-y-1">
        <p className={cn('text-sm font-medium', mutedLabel && 'text-muted-foreground')}>{label}</p>
        <p className="text-sm text-muted-foreground">{help}</p>
      </div>
      <div className="flex w-[240px] shrink-0 justify-end">{children}</div>
    </div>
  );
}
