import { useState } from 'react';

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
      const res = await fetch('/api/sessions/couple', {
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
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40">
      <div className="bg-background rounded-lg shadow-xl p-6 w-full max-w-sm">
        <h2 className="text-lg font-semibold mb-4">Couple Device</h2>
        <p className="text-sm text-muted-foreground mb-4">
          Enter the 6-character passcode displayed on the Cloud Imaging Client.
        </p>
        <form onSubmit={handleSubmit}>
          <input
            type="text"
            maxLength={6}
            placeholder="e.g. ABC123"
            value={passcode}
            onChange={e => setPasscode(e.target.value.toUpperCase())}
            className="w-full border border-input rounded-md px-3 py-2 text-xl font-mono tracking-widest text-center mb-3 focus:outline-none focus:ring-2 focus:ring-primary"
            autoFocus
            disabled={busy}
          />
          {error && (
            <p className="text-sm text-destructive mb-3">{error}</p>
          )}
          <div className="flex gap-3 justify-end">
            <button
              type="button"
              onClick={handleClose}
              disabled={busy}
              className="px-4 py-2 text-sm border border-border rounded-md hover:bg-muted"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={busy || passcode.length < 6}
              className="px-4 py-2 text-sm bg-primary text-primary-foreground rounded-md hover:bg-primary/90 disabled:opacity-50"
            >
              {busy ? 'Coupling…' : 'Couple Device'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
