import { Badge } from '../../ui';
import type { Project } from '../../api/projects';

/** Shown wherever projects are listed, so a paused project is never mistaken for a stale one. */
export function PausedBadge({ project }: { project: Pick<Project, 'syncPaused'> }) {
  return project.syncPaused ? (
    <Badge tone="warning" title="Scheduled sync is paused">
      Paused
    </Badge>
  ) : null;
}
