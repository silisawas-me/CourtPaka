import en from './en.json';
import th from './th.json';

export const LANGUAGES = ['th', 'en'] as const;

export type Language = (typeof LANGUAGES)[number];

export const DEFAULT_LANGUAGE: Language = 'th';

/** Flat key/value dictionaries; th is the source of truth and en must match it key for key. */
export const TRANSLATIONS: Record<Language, Record<string, string>> = { th, en };

export function isLanguage(value: string | null | undefined): value is Language {
  return LANGUAGES.includes(value as Language);
}
