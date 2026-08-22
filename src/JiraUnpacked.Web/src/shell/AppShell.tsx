import { Outlet, useLocation } from 'react-router-dom';
import { ErrorBoundary, ThemeToggle } from '../ui';
import { SideNav } from './SideNav';
import { UserMenu } from './UserMenu';

export default function AppShell() {
  const location = useLocation();

  return (
    <div className="flex min-h-screen flex-col md:grid md:grid-cols-[14rem_1fr] md:grid-rows-[auto_1fr]">
      <div className="md:col-start-1 md:row-span-2">
        <SideNav />
      </div>
      <header className="flex items-center justify-end gap-2 border-b border-[var(--ju-border)] p-3 md:col-start-2 md:row-start-1">
        <ThemeToggle />
        <UserMenu />
      </header>
      <main className="flex-1 p-4 md:col-start-2 md:row-start-2 md:overflow-y-auto">
        {/*
          Inside the shell, not around it: a page that throws leaves the nav and
          the header intact, so the user can navigate away instead of reloading.
          Keyed on the path so the error clears when they do.
        */}
        <ErrorBoundary resetKey={location.pathname}>
          <Outlet />
        </ErrorBoundary>
      </main>
    </div>
  );
}
