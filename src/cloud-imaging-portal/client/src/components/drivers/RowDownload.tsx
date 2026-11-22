import React from 'react';
import { CircleX, Clock, Download } from 'lucide-react';
import { Tooltip } from '../ui/tooltip.tsx';

/** A component row whose file is on its way: the icon beside its chevron and the line under its name. */
export interface RowDownload {
  status: 'Waiting' | 'Downloading' | 'Failed';
  /** Shown under the name instead of the component's id and category. */
  text: string;
  /** Why it failed: the icon's tooltip. */
  reason?: string | null;
}

/** The icon beside the chevron. A failure's reason is its tooltip. */
export function RowDownloadIcon({ download }: { download: RowDownload }): React.ReactElement {
  if (download.status === 'Failed') {
    const label = download.reason ?? download.text;
    return (
      <Tooltip content={label}>
        <span tabIndex={0} aria-label={label} className="inline-flex shrink-0">
          <CircleX className="h-4 w-4 text-destructive" aria-hidden="true" />
        </span>
      </Tooltip>
    );
  }
  return download.status === 'Waiting'
    ? <Clock className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden="true" />
    : <Download className="h-4 w-4 shrink-0 text-blue-600 dark:text-blue-400" aria-hidden="true" />;
}

/** A bar moving along the bottom edge of a downloading row, whose `tr` is `relative`; it stands still for reduced motion. */
export function RowDownloadEdge({ label }: { label: string }): React.ReactElement {
  return (
    <div role="progressbar" aria-label={label} className="pointer-events-none absolute inset-x-0 bottom-0 h-0.5 overflow-hidden bg-blue-500/20">
      <div className="h-full w-1/3 bg-blue-500 motion-safe:animate-loading-bar" />
    </div>
  );
}
