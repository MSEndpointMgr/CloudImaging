import { useState } from 'react';
import { ChevronDown, ChevronRight } from 'lucide-react';

interface ImagingStep {
  stepName: string;
  status: string;
  stepProgressPercent?: number;
  startedAt?: string;
  completedAt?: string;
  errorDetail?: string;
}

interface SessionProgressRowProps {
  sessionId: string;
  deviceSerial: string;
  state: string;
  overallPercent: number;
  currentStep: string | null;
  steps?: ImagingStep[];
}

function stepBadge(status: string): string {
  switch (status) {
    case 'Completed': return 'bg-green-100 text-green-800';
    case 'InProgress': return 'bg-blue-100 text-blue-800';
    case 'Failed':    return 'bg-red-100 text-red-800';
    default:          return 'bg-gray-100 text-gray-700';
  }
}

/**
 * Session progress row with inline expandable step detail panel (T079, FR-007).
 */
export function SessionProgressRow({
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  sessionId: _sessionId, deviceSerial, state, overallPercent, currentStep, steps = [],
}: SessionProgressRowProps): React.ReactElement {
  const [expanded, setExpanded] = useState(false);

  return (
    <>
      <tr className="border-t border-border hover:bg-muted/30">
        <td className="px-3 py-2">
          <button onClick={() => setExpanded(e => !e)} className="text-muted-foreground hover:text-foreground">
            {expanded ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
          </button>
        </td>
        <td className="px-3 py-2 font-mono text-xs">{deviceSerial}</td>
        <td className="px-3 py-2 text-sm">{state.replace('Session', '')}</td>
        <td className="px-3 py-2">
          {overallPercent > 0 ? (
            <div className="flex items-center gap-2">
              <div className="w-24 bg-muted rounded-full h-1.5">
                <div className="bg-primary h-1.5 rounded-full" style={{ width: `${overallPercent}%` }} />
              </div>
              <span className="text-xs text-muted-foreground">{overallPercent}%</span>
            </div>
          ) : '—'}
        </td>
        <td className="px-3 py-2 text-xs text-muted-foreground">{currentStep ?? '—'}</td>
      </tr>
      {expanded && (
        <tr className="bg-muted/20">
          <td colSpan={5} className="px-6 py-3">
            {steps.length === 0 ? (
              <p className="text-xs text-muted-foreground">No step details available.</p>
            ) : (
              <div className="space-y-1">
                {steps.map(step => (
                  <div key={step.stepName} className="flex items-center gap-3 text-xs">
                    <span className={`inline-flex rounded-full px-2 py-0.5 font-medium ${stepBadge(step.status)}`}>
                      {step.status}
                    </span>
                    <span className="font-medium">{step.stepName}</span>
                    {step.stepProgressPercent !== undefined && step.status === 'InProgress' && (
                      <span className="text-muted-foreground">{step.stepProgressPercent}%</span>
                    )}
                    {step.errorDetail && (
                      <span className="text-destructive truncate max-w-xs" title={step.errorDetail}>
                        {step.errorDetail}
                      </span>
                    )}
                  </div>
                ))}
              </div>
            )}
          </td>
        </tr>
      )}
    </>
  );
}
