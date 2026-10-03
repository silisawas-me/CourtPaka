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

/**
 * Thai, which is the source of truth and what almost every visitor reads. It ships with the app;
 * any other language is fetched when somebody asks for it, because a Thai reader waiting on a
 * court grid should not be downloading English first (PRD 8's LCP target, US-23).
 *
 * `locales.spec.ts` is what keeps the other languages honest: same keys, nothing empty.
 */
export const THAI: Record<string, string> = th;

/** One language's words, fetched if they are not the ones that ship with the app. */
export async function dictionaryFor(language: Language): Promise<Record<string, string>> {
  return language === DEFAULT_LANGUAGE ? THAI : (await import('./en.json')).default;
}

export function isLanguage(value: string | null | undefined): value is Language {
  return LANGUAGES.includes(value as Language);
}
