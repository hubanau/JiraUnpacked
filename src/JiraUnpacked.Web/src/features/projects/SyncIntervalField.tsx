import { Field } from '../../ui';
import { MIN_SYNC_INTERVAL_MINUTES } from '../../api/projects';

interface SyncIntervalFieldProps {
  value: number;
  onChange: (v: number) => void;
  error?: string;
}

/**
 * Reusable numeric field shared by AddProject and ProjectDetail. The
 * client-side minimum check below is a hint that saves a round-trip — the
 * server remains authoritative, so callers must still render a server 400
 * when one occurs rather than assuming the client check already covered it.
 */
export function SyncIntervalField({ value, onChange, error }: SyncIntervalFieldProps) {
  const tooLow = value < MIN_SYNC_INTERVAL_MINUTES;
  return (
    <Field
      label="Sync interval (minutes)"
      id="sync-interval"
      hint={`Minimum ${MIN_SYNC_INTERVAL_MINUTES} minutes.`}
      error={error ?? (tooLow ? `Sync interval must be at least ${MIN_SYNC_INTERVAL_MINUTES} minutes.` : undefined)}
    >
      <input
        id="sync-interval"
        type="number"
        min={MIN_SYNC_INTERVAL_MINUTES}
        value={value}
        onChange={(e) => onChange(Number(e.target.value))}
      />
    </Field>
  );
}
