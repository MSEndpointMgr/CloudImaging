interface UploadProgressBarProps {
  percent: number;
  label?: string;
  error?: string | null;
}

/**
 * Reusable upload progress bar with label and error state (T128, FR-063).
 */
export function UploadProgressBar({ percent, label, error }: UploadProgressBarProps): React.ReactElement {
  return (
    <div className="w-full space-y-1">
      {label && (
        <div className="flex items-center justify-between text-xs">
          <span className="text-muted-foreground">{label}</span>
          <span className="font-medium">{percent}%</span>
        </div>
      )}
      <div className="w-full bg-muted rounded-full h-2">
        <div
          className={['h-2 rounded-full transition-all', error ? 'bg-destructive' : 'bg-primary'].join(' ')}
          style={{ width: `${percent}%` }}
        />
      </div>
      {error && <p className="text-sm text-destructive">{error}</p>}
    </div>
  );
}
