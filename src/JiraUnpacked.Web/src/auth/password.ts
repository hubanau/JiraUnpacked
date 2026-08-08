/**
 * Client mirror of src/JiraUnpacked.Domain/Security/PasswordPolicy.cs.
 * This is a hint that saves a round-trip — the server remains authoritative
 * and its validation errors are shown verbatim when they occur. The Unicode
 * property escapes match .NET's Unicode-aware char.IsLower/IsUpper/IsDigit.
 */
export const MIN_LENGTH = 12;
export const MIN_CLASSES = 3;

export interface PasswordAssessment {
  longEnough: boolean;
  classes: number;
  enoughClasses: boolean;
  valid: boolean;
}

export function assessPassword(password: string): PasswordAssessment {
  const longEnough = password.length >= MIN_LENGTH;

  let classes = 0;
  if (/\p{Ll}/u.test(password)) classes++;
  if (/\p{Lu}/u.test(password)) classes++;
  if (/\p{Nd}/u.test(password)) classes++;
  if (/[^\p{L}\p{Nd}]/u.test(password)) classes++;

  const enoughClasses = classes >= MIN_CLASSES;
  return { longEnough, classes, enoughClasses, valid: longEnough && enoughClasses };
}
