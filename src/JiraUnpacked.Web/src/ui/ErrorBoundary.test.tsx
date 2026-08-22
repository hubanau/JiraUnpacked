import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ErrorBoundary } from './ErrorBoundary';

function Boom({ explode }: { explode: boolean }) {
  if (explode) throw new Error('render exploded');
  return <p>content</p>;
}

// React logs caught render errors to console.error. Silenced so an expected
// failure does not look like a broken test run.
let consoleError: ReturnType<typeof vi.spyOn>;
beforeEach(() => {
  consoleError = vi.spyOn(console, 'error').mockImplementation(() => {});
});
afterEach(() => consoleError.mockRestore());

describe('ErrorBoundary', () => {
  it('renders its children when nothing throws', () => {
    render(<ErrorBoundary><Boom explode={false} /></ErrorBoundary>);

    expect(screen.getByText('content')).toBeInTheDocument();
  });

  it('shows a recoverable error instead of unmounting the tree', () => {
    render(<ErrorBoundary><Boom explode /></ErrorBoundary>);

    // Without a boundary React unmounts everything, leaving a blank page whose
    // only escape is a manual reload. Something must remain on screen.
    expect(screen.getByRole('alert')).toHaveTextContent('Something went wrong');
    expect(screen.getByRole('button', { name: /retry/i })).toBeInTheDocument();
  });

  it('recovers when the retry succeeds', async () => {
    // A plain flag rather than component state: the boundary unmounts its
    // children when it catches, so anything held inside them is gone by the
    // time Retry re-renders.
    let broken = true;
    function Flaky() {
      if (broken) throw new Error('transient');
      return <p>recovered</p>;
    }

    render(<ErrorBoundary><Flaky /></ErrorBoundary>);
    expect(screen.getByRole('alert')).toBeInTheDocument();

    broken = false;
    await userEvent.click(screen.getByRole('button', { name: /retry/i }));

    expect(await screen.findByText('recovered')).toBeInTheDocument();
  });

  it('clears the error when the reset key changes', () => {
    const { rerender } = render(
      <ErrorBoundary resetKey="/projects/a"><Boom explode /></ErrorBoundary>,
    );
    expect(screen.getByRole('alert')).toBeInTheDocument();

    // The shell passes the route path. Without this, one bad render would
    // poison every later route: the user navigates and still sees the old
    // failure, with no way back short of a reload.
    rerender(
      <ErrorBoundary resetKey="/projects/b"><Boom explode={false} /></ErrorBoundary>,
    );

    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByText('content')).toBeInTheDocument();
  });

  it('keeps showing the error while the reset key is unchanged', () => {
    const { rerender } = render(
      <ErrorBoundary resetKey="/same"><Boom explode /></ErrorBoundary>,
    );

    rerender(<ErrorBoundary resetKey="/same"><Boom explode={false} /></ErrorBoundary>);

    // A re-render on the same route is not evidence the problem is gone, so the
    // boundary must not clear itself on any incidental parent update.
    expect(screen.getByRole('alert')).toBeInTheDocument();
  });
});
