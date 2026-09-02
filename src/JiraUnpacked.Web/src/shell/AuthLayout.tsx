import type { ReactNode } from 'react';
import { Card, Logo } from '../ui';

interface AuthLayoutProps {
  title: string;
  /** One line under the title saying what this screen is for. */
  description?: string;
  children: ReactNode;
  /** Sign-in / register cross-link, shown under the card. */
  footer?: ReactNode;
}

/**
 * The shell for the screens reached before signing in. These are the first thing
 * anyone sees, so the mark is large here and small everywhere else — the app
 * introduces itself once, then gets out of the way.
 *
 * Deliberately outside AppShell: there is no nav to show, and the centred column
 * is what makes a short form look intentional rather than stranded top-left.
 */
export function AuthLayout({ title, description, children, footer }: AuthLayoutProps) {
  return (
    <div className="flex min-h-screen flex-col items-center justify-center bg-[var(--ju-bg)] px-4 py-10">
      <div className="w-full max-w-sm">
        <div className="mb-7 flex flex-col items-center text-center">
          <Logo size={44} />
          <p className="mt-3 text-lg font-medium tracking-tight text-[var(--ju-text)]">
            Jira Unpacked
          </p>
          <p className="mt-1 text-sm text-[var(--ju-muted)]">
            Your Jira data, kept and queryable.
          </p>
        </div>

        <Card>
          <h1 className="mb-1 text-base font-medium text-[var(--ju-text)]">{title}</h1>
          {description && (
            <p className="mb-4 text-sm text-[var(--ju-muted)]">{description}</p>
          )}
          {!description && <div className="mb-4" />}
          {children}
        </Card>

        {footer && (
          <p className="mt-4 text-center text-sm text-[var(--ju-muted)]">{footer}</p>
        )}
      </div>
    </div>
  );
}
