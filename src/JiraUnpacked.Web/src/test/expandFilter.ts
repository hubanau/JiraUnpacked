import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

/**
 * Opens the filter builder. A list opened from a link or a saved view WITH a filter shows only its
 * summary sentence until "Edit filter" is clicked — the builder is not mounted before that.
 */
export async function expandFilter(): Promise<void> {
  await userEvent.click(await screen.findByRole('button', { name: 'Edit filter' }));
}
