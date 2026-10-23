import type { LucideIcon } from 'lucide-react';
import { Card, CardContent } from '../ui/card.tsx';

/**
 * A settings card with the icon chip header: a 36px tinted chip, the title and description beside
 * it, and the section's own action on the right.
 */
export function SectionCard({ icon: Icon, title, description, action, children }: {
  icon: LucideIcon;
  title: string;
  description: string;
  action?: React.ReactNode;
  children: React.ReactNode;
}): React.ReactElement {
  return (
    <Card>
      <div className="flex items-center justify-between gap-4 px-5 py-4">
        <div className="flex min-w-0 items-center gap-3">
          <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-md bg-primary/10 text-primary">
            <Icon className="h-4 w-4" aria-hidden="true" />
          </span>
          <div className="min-w-0">
            <h3 className="text-base font-semibold">{title}</h3>
            <p className="text-sm text-muted-foreground">{description}</p>
          </div>
        </div>
        {action && <div className="shrink-0">{action}</div>}
      </div>
      <CardContent className="px-5 pb-5">{children}</CardContent>
    </Card>
  );
}

/** One row of a settings card: label and helper on the left, the control on the right. */
export function SettingRow({ label, help, htmlFor, children }: {
  label: string;
  help: string;
  htmlFor?: string;
  children: React.ReactNode;
}): React.ReactElement {
  return (
    <div className="flex items-center justify-between gap-6">
      <div className="min-w-0 space-y-1">
        {htmlFor ? <label htmlFor={htmlFor} className="block text-sm font-medium">{label}</label> : <p className="text-sm font-medium">{label}</p>}
        <p className="text-xs text-muted-foreground">{help}</p>
      </div>
      <div className="flex shrink-0 justify-end">{children}</div>
    </div>
  );
}
