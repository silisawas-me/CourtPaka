import en from './en.json';
import th from './th.json';

/**
 * What each language calls a date. Thai reads years in the Buddhist era, which is what Intl gives
 * for th-TH; every date on screen and every datepicker goes through this one map, so a page and
 * the picker beside it can never disagree.
 */
export const DATE_LOCALES: Record<Language, string> = {
  th: 'th-TH',
  en: 'en-GB',
};

export const LANGUAGES = ['th', 'en'] as const;

export type Language = (typeof LANGUAGES)[number];

export const DEFAULT_LANGUAGE: Language = 'th';

/** Flat key/value dictionaries; th is the source of truth and en must match it key for key. */
export const TRANSLATIONS: Record<Language, Record<string, string>> = { th, en };

export function isLanguage(value: string | null | undefined): value is Language {
  return LANGUAGES.includes(value as Language);
}
