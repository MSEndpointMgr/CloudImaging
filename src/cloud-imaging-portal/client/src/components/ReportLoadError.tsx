import * as React from 'react';
import { AlertTriangle, RefreshCw } from 'lucide-react';
import { Button } from './ui/button.tsx';

/** A report whose data did not load: what failed, why, and Try again. */
export function ReportLoadError({ title, detail, onRetry }: { title: string; detail: string; onRetry: () => void }): React.ReactElement {
  return (
    <div role="alert" className="flex items-center gap-3 rounded-md border border-destructive/40 bg-destructive/5 p-4">
      <AlertTriangle className="h-4 w-4 shrink-0 text-destructive" aria-hidden="true" />
      <div className="flex-1 space-y-1">
        <p className="text-sm font-medium">{title}</p>
        <p className="text-sm text-muted-foreground">{detail}</p>
      </div>
      <Button variant="outline" onClick={onRetry}><RefreshCw /> Try again</Button>
    </div>
  );
}
