import * as React from 'react';
import { cn } from '../../lib/utils.ts';

/**
 * Monospace text area with a line number gutter that scrolls with the text, for scripts and
 * answer files. The label is required because the editor has no visible text of its own; problems
 * are linked through `describedBy` so a screen reader reads them with the field.
 */
export const CodeTextArea = React.forwardRef<HTMLTextAreaElement, {
  id: string;
  label: string;
  value: string;
  onChange: (value: string) => void;
  /** Visible lines before the editor scrolls. */
  rows?: number;
  readOnly?: boolean;
  invalid?: boolean;
  describedBy?: string;
}>(({ id, label, value, onChange, rows = 12, readOnly = false, invalid = false, describedBy }, ref) => {
  const gutter = React.useRef<HTMLDivElement>(null);
  const lineCount = Math.max(rows, value.split('\n').length);
  const numbers = React.useMemo(() => Array.from({ length: lineCount }, (_, i) => String(i + 1)).join('\n'), [lineCount]);

  return (
    <div
      className={cn(
        'grid grid-cols-[40px_minmax(0,1fr)] overflow-hidden rounded-md border bg-background font-mono text-xs leading-5 focus-within:ring-2 focus-within:ring-ring focus-within:ring-offset-2 focus-within:ring-offset-background',
        invalid ? 'border-destructive' : 'border-input',
      )}
      style={{ height: `${String(rows * 20 + 16)}px` }}
    >
      <div ref={gutter} aria-hidden="true" className="overflow-hidden border-r border-border bg-muted/30 py-2 pr-2 text-right text-muted-foreground">
        <pre className="m-0 whitespace-pre font-mono">{numbers}</pre>
      </div>
      <textarea
        ref={ref}
        id={id}
        aria-label={label}
        aria-describedby={describedBy}
        aria-invalid={invalid || undefined}
        value={value}
        readOnly={readOnly}
        spellCheck={false}
        wrap="off"
        onChange={(e) => onChange(e.target.value.replace(/\r\n/g, '\n'))}
        onScroll={(e) => { if (gutter.current) gutter.current.scrollTop = e.currentTarget.scrollTop; }}
        className="h-full w-full resize-none select-text overflow-auto whitespace-pre bg-transparent px-3 py-2 text-foreground outline-none"
      />
    </div>
  );
});
CodeTextArea.displayName = 'CodeTextArea';
