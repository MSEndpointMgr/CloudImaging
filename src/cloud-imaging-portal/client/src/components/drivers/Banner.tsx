import React from 'react';

export type BannerTone = 'neutral' | 'warning' | 'info';

const BANNER_TONES: Record<BannerTone, { box: string; icon: string }> = {
  neutral: { box: 'border-border bg-muted/30', icon: 'text-muted-foreground' },
  warning: { box: 'border-amber-300 bg-amber-50 dark:border-amber-900 dark:bg-amber-950/40', icon: 'text-amber-600 dark:text-amber-400' },
  info: { box: 'border-blue-200 bg-blue-50 dark:border-blue-900 dark:bg-blue-950/40', icon: 'text-blue-600 dark:text-blue-400' },
};

/** A banner under the model page header: icon centred on its text, actions on the right. */
export function Banner({ icon, title, detail, tone = 'neutral', children }: { icon: React.ReactNode; title: string; detail: string; tone?: BannerTone; children?: React.ReactNode }): React.ReactElement {
  return (
    <div role="status" className={`flex items-center gap-4 rounded-lg border px-4 py-3 ${BANNER_TONES[tone].box}`}>
      <span className={`shrink-0 ${BANNER_TONES[tone].icon}`}>{icon}</span>
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium">{title}</p>
        <p className="text-sm text-muted-foreground">{detail}</p>
      </div>
      {children}
    </div>
  );
}
