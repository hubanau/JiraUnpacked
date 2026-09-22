/**
 * Client mirror of src/JiraUnpacked.Domain/Security/AccountInputRules.cs.
 * A hint that saves a round-trip: the server stays authoritative (its email check is a full
 * address parse; this one only catches the obvious mistakes) and its problem title is shown
 * verbatim when it disagrees.
 */
export const EMAIL_MAX_LENGTH = 254;
export const USERNAME_MIN_LENGTH = 3;
export const USERNAME_MAX_LENGTH = 32;

const USERNAME_PATTERN = /^[A-Za-z0-9._-]{3,32}$/;
// Exactly one '@' with something on each side, and no whitespace or display-name brackets.
// Deliberately loose: anything subtler is the server's call.
const EMAIL_SHAPE = /^[^\s@<>]+@[^\s@<>]+$/;

export const USERNAME_HINT =
  `${USERNAME_MIN_LENGTH}–${USERNAME_MAX_LENGTH} letters, digits, '.', '_' or '-'.`;

/** A message describing what is wrong with the email, or null when it looks acceptable. */
export function emailProblem(email: string): string | null {
  const trimmed = email.trim();
  if (trimmed.length === 0) return 'Enter your email address.';
  if (trimmed.length > EMAIL_MAX_LENGTH) return `Email must be at most ${EMAIL_MAX_LENGTH} characters.`;
  if (!EMAIL_SHAPE.test(trimmed)) return 'Enter a valid email address, such as name@example.com.';
  return null;
}

/** A message describing what is wrong with the username, or null when it is acceptable. */
export function usernameProblem(username: string): string | null {
  if (username.includes('@')) return "A username can't contain '@'.";
  if (!USERNAME_PATTERN.test(username)) return `Use ${USERNAME_HINT}`;
  return null;
}
