import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { AuthProvider } from '../auth/AuthContext';
import { UserMenu } from './UserMenu';

function renderMenu() {
  return render(
    <AuthProvider>
      <UserMenu />
    </AuthProvider>,
  );
}

async function openDeleteDialog(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole('button', { name: /account/i }));
  await user.click(screen.getByRole('menuitem', { name: /delete account/i }));
}

describe('UserMenu delete-account dialog', () => {
  // Regression for: open the dialog, type DELETE, hesitate, press Escape,
  // reopen — the confirm gate must not still be armed. Before the fix the
  // dialog was mounted unconditionally, so its `typed`/`error` state
  // survived the close and the confirm button stayed enabled on reopen.
  it('resets the typed confirmation and disables confirm after closing and reopening', async () => {
    const user = userEvent.setup();
    renderMenu();

    await openDeleteDialog(user);
    await user.type(screen.getByLabelText(/type delete to confirm/i), 'DELETE');
    expect(screen.getByRole('button', { name: 'Delete account' })).toBeEnabled();

    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

    await openDeleteDialog(user);

    expect(screen.getByLabelText(/type delete to confirm/i)).toHaveValue('');
    expect(screen.getByRole('button', { name: 'Delete account' })).toBeDisabled();
  });
});
