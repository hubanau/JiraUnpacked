import { NavLink } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { Logo } from '../ui';

// The active state carries a left rail as well as a tint. Colour alone is a weak
// signal for "where am I", and it disappears entirely for anyone who cannot
// distinguish the accent from the body text.
const LINK_CLASS =
  'block rounded-[var(--ju-radius)] border-l-2 border-transparent px-3 py-2 text-sm text-[var(--ju-text-secondary)] transition-colors hover:bg-[var(--ju-surface-2)] hover:text-[var(--ju-text)] aria-[current=page]:border-[var(--ju-primary)] aria-[current=page]:bg-[var(--ju-primary-soft)] aria-[current=page]:font-medium aria-[current=page]:text-[var(--ju-primary)]';

// Projects (Home) and Connections are the areas every role can reach.
// Admin is a UX affordance only — the API enforces the Admin policy on its
// own, so hiding this entry for non-admins just avoids showing a door that
// will not open. It is not a security control.
export function SideNav() {
  const { role } = useAuth();

  return (
    <nav className="flex items-center gap-2 border-b border-[var(--ju-border)] bg-[var(--ju-surface)] p-3 md:h-full md:w-56 md:flex-col md:items-stretch md:border-b-0 md:border-r md:p-4">
      {/* Small mark in the shell; the large one lives on the sign-in screen. */}
      <div className="px-1 md:mb-5">
        <Logo size={20} withWordmark />
      </div>
      <ul className="flex gap-1 md:flex-col">
        <li>
          <NavLink to="/" end className={LINK_CLASS}>
            Projects
          </NavLink>
        </li>
        <li>
          <NavLink to="/connections" className={LINK_CLASS}>
            Connections
          </NavLink>
        </li>
        {role === 'Admin' && (
          <li>
            <NavLink to="/admin" className={LINK_CLASS}>
              Admin
            </NavLink>
          </li>
        )}
      </ul>
    </nav>
  );
}
