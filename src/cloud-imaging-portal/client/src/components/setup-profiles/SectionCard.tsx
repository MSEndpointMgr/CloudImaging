import type { LucideIcon } from 'lucide-react';
import { Card, CardContent } from '../ui/card.tsx';
import { UndoChangeButton } from '../ui/unsaved-change.tsx';
import { unsavedRail } from '../../lib/unsavedChanges.ts';
import { cn } from '../../lib/utils.ts';

/**
 * A settings card with the icon chip header: a 36px tinted chip, the title and description beside
 * it, and the section's own action on the right.
 */
export function SectionCard({ icon: Icon, title, description, action, changed = false, onUndo, children }: {
  icon: LucideIcon;
  title: string;
  description: string;
  action?: React.ReactNode;
  /** Marks the section as holding unsaved changes. */
  changed?: boolean;
  /** Reverts the section's unsaved changes, shown beside the action while it has any. */
  onUndo?: () => void;
  children: React.ReactNode;
}): React.ReactElement {
  return (
    <Card className={unsavedRail(changed)}>
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
        {(action || (changed && onUndo)) && (
          <div className="flex shrink-0 items-center gap-2">
            {changed && onUndo && <UndoChangeButton label={title} onUndo={onUndo} />}
            {action}
          </div>
        )}
      </div>
      <CardContent className="px-5 pb-5">{children}</CardContent>
    </Card>
  );
}

/** One row of a settings card: label and helper on the left, the control on the right. */
export function SettingRow({ label, help, htmlFor, changed = false, onUndo, children }: {
  label: string;
  help: string;
  htmlFor?: string;
  /** Marks the row as holding an unsaved change. */
  changed?: boolean;
  onUndo?: () => void;
  children: React.ReactNode;
}): React.ReactElement {
  return (
    <div className={cn('flex items-center justify-between gap-6', unsavedRail(changed, 'bare'))}>
      <div className="min-w-0 space-y-1">
        {htmlFor ? <label htmlFor={htmlFor} className="block text-sm font-medium">{label}</label> : <p className="text-sm font-medium">{label}</p>}
        <p className="text-xs text-muted-foreground">{help}</p>
      </div>
      <div className="flex shrink-0 items-center justify-end gap-2">
        {changed && onUndo && <UndoChangeButton label={label} onUndo={onUndo} />}
        {children}
      </div>
    </div>
  );
}
