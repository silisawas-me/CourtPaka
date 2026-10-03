import en from '../core/i18n/en.json';
import th from '../core/i18n/th.json';
import { Language } from '../core/i18n/locales';

/**
 * Both dictionaries, for tests to check a screen against.
 *
 * Not in `locales.ts`, which the app itself imports: whatever is reachable from there ships in
 * the first download, and a Thai reader should not pay for the English dictionary (PRD 8's LCP
 * target). The app loads the other language when it is asked for; a test can have both at once.
 */
export const TRANSLATIONS: Record<Language, Record<string, string>> = { th, en };
