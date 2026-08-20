import { NavLink } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';

const LINK_CLASS =
  'block rounded-md px-3 py-2 text-sm font-medium text-[var(--ju-text)] hover:bg-[var(--ju-surface)] aria-[current=page]:bg-[var(--ju-surface)] aria-[current=page]:text-[var(--ju-primary)]';

// Projects (Home) and Connections are the areas every role can reach.
// Admin is a UX affordance only — the API enforces the Admin policy on its
// own, so hiding this entry for non-admins just avoids showing a door that
// will not open. It is not a security control.
export function SideNav() {
  const { role } = useAuth();

  return (
    <nav className="flex items-center gap-2 border-b border-[var(--ju-border)] p-3 md:h-full md:w-56 md:flex-col md:items-stretch md:border-b-0 md:border-r md:p-4">
      <p className="hidden px-3 text-sm font-semibold text-[var(--ju-text)] md:block">Jira Unpacked</p>
      <ul className="flex gap-1 md:mt-4 md:flex-col">
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
