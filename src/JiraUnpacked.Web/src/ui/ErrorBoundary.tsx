import { Component, type ErrorInfo, type ReactNode } from 'react';
import { ErrorState } from './ErrorState';

interface ErrorBoundaryProps {
  children: ReactNode;
  /**
   * Changing this value clears the caught error. Pass the current route so a
   * user who navigates away is not stuck looking at a failure from the page
   * they left — without it, one bad render poisons every later route too.
   */
  resetKey?: string;
}

interface ErrorBoundaryState {
  message: string | null;
}

/**
 * Catches render-time throws. Without one, React unmounts the entire tree on
 * any uncaught render error: the shell, the nav and the router all disappear,
 * leaving a blank page whose only escape is a manual reload.
 *
 * Class component because that is the only form React offers for this — there
 * is no hook equivalent.
 */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { message: null };

  static getDerivedStateFromError(error: unknown): ErrorBoundaryState {
    return { message: error instanceof Error ? error.message : 'Unexpected error.' };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    console.error('Unhandled render error', error, info.componentStack);
  }

  componentDidUpdate(prev: ErrorBoundaryProps): void {
    if (this.state.message !== null && prev.resetKey !== this.props.resetKey) {
      this.setState({ message: null });
    }
  }

  render(): ReactNode {
    if (this.state.message === null) return this.props.children;

    return (
      <ErrorState
        title="Something went wrong on this page."
        // The message is shown because this app is self-hosted and its users are
        // the people who can act on it. It is a client-side render error, so it
        // carries no server detail a user could not already see.
        detail={this.state.message}
        onRetry={() => this.setState({ message: null })}
      />
    );
  }
}
