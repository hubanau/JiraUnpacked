import type { ButtonHTMLAttributes } from 'react';
import type { FilterCatalog, FilterLogic } from '../../api/issues';
import { Button, Select } from '../../ui';
import { ConditionRow } from './ConditionRow';
import {
  blankCondition, blankGroup, cloneCondition, cloneGroup, countEnabled, insertAfter, toggleDisabled,
  withIdentityOf,
  type EditorGroup, type TreeCounts,
} from './filterTree';

interface GroupEditorProps {
  group: EditorGroup;
  onChange: (next: EditorGroup) => void;
  catalog: FilterCatalog;
  projectId: string;
  /** Enabled conditions and groups over the WHOLE tree: the limits are global, as on the server. */
  counts: TreeCounts;
  /** Root = 1. "Add group" is offered only while a child would still be within catalog.maxDepth. */
  depth: number;
  /** Position label, e.g. "2" or "2.1"; empty for the root. Part of every accessible name. */
  label: string;
  /** Absent at the root: the root is cleared ("Clear all"), never removed or duplicated. */
  onRemove?: () => void;
  onDuplicate?: () => void;
}

type IconButtonProps = { label: string; glyph: string } &
  Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'children'>;

/** A compact button whose accessible name is `label`; the glyph is decoration. */
function IconButton({ label, glyph, ...rest }: IconButtonProps) {
  return (
    <Button type="button" variant="ghost" size="sm" aria-label={label} {...rest}>
      <span aria-hidden="true">{glyph}</span>
    </Button>
  );
}

/**
 * One boolean group: its match (All/Any), its condition rows, its nested groups (recursively),
 * and the add buttons. Holds no state — every edit rebuilds this group and hands it up.
 *
 * Conditions render before nested groups because the wire keeps them in separate lists; logic
 * is commutative, so the order is presentation only.
 */
export function GroupEditor({
  group, onChange, catalog, projectId, counts, depth, label, onRemove, onDuplicate,
}: GroupEditorProps) {
  const isRoot = depth === 1;
  const name = isRoot ? 'filter' : `group ${label}`;
  const childLabel = (position: number) => (isRoot ? String(position) : `${label}.${position}`);
  const { conditions, groups } = group;

  /** Why adding `extra` would break a limit, as the hint the disabled button shows; undefined = it fits. */
  const limitHint = (extra: TreeCounts): string | undefined => {
    if (counts.conditions + extra.conditions > catalog.maxConditions) return `Limit: ${catalog.maxConditions} conditions`;
    if (counts.groups + extra.groups > catalog.maxGroups) return `Limit: ${catalog.maxGroups} groups`;
    return undefined;
  };
  const addConditionHint = limitHint({ conditions: 1, groups: 0, history: 0 });
  // A new group arrives with one blank condition, so it needs room for both.
  const addGroupHint = limitHint({ conditions: 1, groups: 1, history: 0 });
  const duplicateHint = limitHint(countEnabled(group));
  // Switching a disabled group back on adds back whatever it holds (respecting ITS children's own
  // on/off state) — counted as if only its own `disabled` flag had already flipped.
  const enableGroupHint = group.disabled ? limitHint(countEnabled({ ...group, disabled: false })) : undefined;

  const set = (patch: Partial<EditorGroup>) => onChange({ ...group, ...patch });

  return (
    <div
      role="group"
      aria-label={isRoot ? 'Filter' : `Group ${label}`}
      className={[
        'flex flex-col gap-2',
        isRoot ? '' : 'ml-4 border-l-2 border-[var(--ju-primary)] pl-3',
        group.disabled ? 'opacity-50' : '',
      ].join(' ')}
    >
      <div className="flex items-center gap-2">
        <label htmlFor={`${group.id}-logic`} className="text-sm font-medium text-[var(--ju-text)]">
          {isRoot ? 'Match' : `Match (group ${label})`}
        </label>
        <Select
          id={`${group.id}-logic`}
          value={group.logic}
          title="All: every part must hold. Any: at least one."
          onChange={(e) => set({ logic: e.target.value as FilterLogic })}
        >
          <option value="And">All</option>
          <option value="Or">Any</option>
        </Select>
        <span className="flex-1" />
        {onDuplicate && (
          <IconButton
            label={`Duplicate ${name}`}
            glyph="⎘"
            disabled={duplicateHint !== undefined}
            title={duplicateHint ?? 'Duplicate'}
            onClick={onDuplicate}
          />
        )}
        <IconButton
          label={`Disable ${name}`}
          glyph="⏻"
          aria-pressed={!!group.disabled}
          disabled={enableGroupHint !== undefined}
          title={group.disabled
            ? (enableGroupHint ?? 'Switched off — click to apply again')
            : 'Switch off without deleting'}
          onClick={() => onChange(toggleDisabled(group))}
        />
        {onRemove && <IconButton label={`Remove ${name}`} glyph="✕" title="Remove" onClick={onRemove} />}
      </div>

      {conditions.map((c, i) => {
        const rowLabel = childLabel(i + 1);
        // Copying a switched-off row adds nothing that counts, so it is never blocked.
        const copyHint = c.disabled ? undefined : addConditionHint;
        // Switching a disabled condition back on adds exactly one condition, same as adding a new one.
        const enableHint = c.disabled ? addConditionHint : undefined;
        return (
          <div
            key={c.id}
            className={`flex items-end gap-1 rounded-md border border-[var(--ju-border)] p-2 ${c.disabled ? 'opacity-50' : ''}`}
          >
            <IconButton
              label={`Disable condition ${rowLabel}`}
              glyph="⏻"
              aria-pressed={!!c.disabled}
              disabled={enableHint !== undefined}
              title={c.disabled
                ? (enableHint ?? 'Switched off — click to apply again')
                : 'Switch off without deleting'}
              onClick={() => set({ conditions: conditions.map((x) => (x.id === c.id ? toggleDisabled(x) : x)) })}
            />
            <div className="flex-1">
              <ConditionRow
                projectId={projectId}
                catalog={catalog}
                condition={c}
                label={rowLabel}
                onChange={(next) =>
                  set({ conditions: conditions.map((x) => (x.id === c.id ? withIdentityOf(x, next) : x)) })}
              />
            </div>
            <IconButton
              label={`Duplicate condition ${rowLabel}`}
              glyph="⎘"
              disabled={copyHint !== undefined}
              title={copyHint ?? 'Duplicate'}
              onClick={() => set({ conditions: insertAfter(conditions, c.id, cloneCondition(c)) })}
            />
            <IconButton
              label={`Remove condition ${rowLabel}`}
              glyph="✕"
              title="Remove"
              onClick={() => set({ conditions: conditions.filter((x) => x.id !== c.id) })}
            />
          </div>
        );
      })}

      {groups.map((g, i) => (
        <GroupEditor
          key={g.id}
          group={g}
          catalog={catalog}
          projectId={projectId}
          counts={counts}
          depth={depth + 1}
          label={childLabel(conditions.length + i + 1)}
          onChange={(next) => set({ groups: groups.map((x) => (x.id === g.id ? next : x)) })}
          onRemove={() => set({ groups: groups.filter((x) => x.id !== g.id) })}
          onDuplicate={() => set({ groups: insertAfter(groups, g.id, cloneGroup(g)) })}
        />
      ))}

      <div className="flex items-center gap-2">
        <Button
          type="button"
          variant="secondary"
          size="sm"
          aria-label={isRoot ? undefined : `Add condition to group ${label}`}
          disabled={addConditionHint !== undefined}
          title={addConditionHint}
          onClick={() => set({ conditions: [...conditions, blankCondition()] })}
        >
          Add condition
        </Button>
        {depth < catalog.maxDepth && (
          <Button
            type="button"
            variant="secondary"
            size="sm"
            aria-label={isRoot ? undefined : `Add group to group ${label}`}
            disabled={addGroupHint !== undefined}
            title={addGroupHint}
            onClick={() => set({ groups: [...groups, blankGroup()] })}
          >
            Add group
          </Button>
        )}
      </div>
    </div>
  );
}
