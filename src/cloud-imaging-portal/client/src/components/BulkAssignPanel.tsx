import { useState } from 'react';
import { AssignImageDialog } from './AssignImageDialog.tsx';
import { apiFetch } from '../lib/apiClient.ts';
import { Button } from './ui/button.tsx';

interface BulkAssignPanelProps {
  /** IDs of sessions that are in SessionAssigned state and currently checked. */
  eligibleSessionIds: string[];
  onBulkAssigned: () => void;
}

/**
 * Context-sensitive bulk assignment panel (T078, FR-035).
 * Appears when ≥ 1 Assigned-state session is checked.
 * Opens the shared AssignImageDialog with N-count subtitle.
 */
export function BulkAssignPanel({ eligibleSessionIds, onBulkAssigned }: BulkAssignPanelProps): React.ReactElement | null {
  const [open, setOpen] = useState(false);

  if (eligibleSessionIds.length === 0) return null;

  const n = eligibleSessionIds.length;

  const handleAssigned = async (_: string, imageId: string) => {
    // Bulk assign: POST with all eligible session IDs
    await apiFetch('/api/sessions/bulk-assign', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'include',
      body: JSON.stringify({ sessionIds: eligibleSessionIds, osImageId: imageId }),
    });
    onBulkAssigned();
  };

  return (
    <div className="flex items-center justify-between rounded-md bg-primary/10 border border-primary/30 px-4 py-2 text-sm">
      <span>
        <strong>{n}</strong> Assigned session{n !== 1 ? 's' : ''} selected
      </span>
      <Button onClick={() => setOpen(true)}>
        Assign Image to {n} Session{n !== 1 ? 's' : ''}
      </Button>

      {/* Reuse the existing AssignImageDialog with the first eligible session as nominal target */}
      <AssignImageDialog
        open={open}
        sessionId={eligibleSessionIds[0] ?? null}
        onClose={() => setOpen(false)}
        onAssigned={(_sid, imgId) => { void handleAssigned('', imgId); setOpen(false); }}
      />
    </div>
  );
}
