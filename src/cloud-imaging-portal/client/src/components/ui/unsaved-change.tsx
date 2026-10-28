import { Undo2 } from 'lucide-react';
import { Button } from './button.tsx';
import { Tooltip } from './tooltip.tsx';

/** Reverts one unsaved setting to its saved value, leaving the rest of the page's changes alone. */
export function UndoChangeButton({ label, onUndo }: { label: string; onUndo: () => void }): React.ReactElement {
  return (
    <Tooltip content="Undo this change">
      <Button type="button" variant="ghost" size="icon" aria-label={`Undo the unsaved change to ${label}`} onClick={onUndo}>
        <Undo2 />
      </Button>
    </Tooltip>
  );
}
