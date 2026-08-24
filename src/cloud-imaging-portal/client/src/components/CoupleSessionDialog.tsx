import { useState } from 'react';
import { apiFetch } from '../lib/apiClient.ts';
import { Button } from './ui/button.tsx';
import { Input } from './ui/input.tsx';

interface CoupleSessionDialogProps {
  open: boolean;
  onClose: () => void;
  onCoupled: (sessionId: string) => void;
}

/**
 * Passcode coupling modal (T042, FR-032).
 * Stays open on failure with inline error; closes on success.
 */
export function CoupleSessionDialog({ open, onClose, onCoupled }: CoupleSessionDialogProps): React.ReactElement | null {
  const [passcode, setPasscode] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  if (!open) return null;

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!passcode.trim()) return;
    setBusy(true);
    setError(null);

    try {
      const res = await apiFetch('/api/sessions/couple', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({ passcode: passcode.toUpperCase().trim() }),
      });

      if (res.status === 201) {
        const data = await res.json() as { sessionId: string };
        setPasscode('');
        onCoupled(data.sessionId);
        onClose();
        return;
      }

      if (res.status === 404) {
        setError('Invalid or expired passcode. Check the passcode on the device and try again.');
      } else if (res.status === 409) {
        setError('This passcode has already been used. The device may already be coupled.');
      } else {
        setError('An unexpected error occurred. Please try again.');
      }
    } catch {
      setError('Network error. Please check your connection and try again.');
    } finally {
      setBusy(false);
    }
  };

  const handleClose = () => {
    setPasscode('');
    setError(null);
    onClose();
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60">
      <div className="bg-background rounded-lg shadow-xl p-6 w-full max-w-sm">
        <h2 className="text-lg font-semibold mb-4">Couple Device</h2>
        <p className="text-sm text-muted-foreground mb-4">
          Enter the 6-character passcode displayed on the Cloud Imaging Client.
        </p>
        <form onSubmit={handleSubmit}>
          <Input
            type="text"
            maxLength={6}
            placeholder="e.g. ABC123"
            value={passcode}
            onChange={e => setPasscode(e.target.value.toUpperCase())}
            className="mb-3 h-12 text-center text-xl font-mono tracking-widest"
            autoFocus
            disabled={busy}
          />
          {error && (
            <p className="text-sm text-destructive mb-3">{error}</p>
          )}
          <div className="flex gap-3 justify-end">
            <Button type="button" variant="outline" onClick={handleClose} disabled={busy}>
              Cancel
            </Button>
            <Button type="submit" disabled={busy || passcode.length < 6}>
              {busy ? 'Coupling…' : 'Couple Device'}
            </Button>
          </div>
        </form>
      </div>
    </div>
  );
}
