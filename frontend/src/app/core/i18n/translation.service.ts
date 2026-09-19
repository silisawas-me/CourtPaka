import { Injectable, signal } from '@angular/core';
import { DEFAULT_LANGUAGE, isLanguage, Language, TRANSLATIONS } from './locales';

export const LANGUAGE_STORAGE_KEY = 'courtpaka.language';

/**
 * Owns the whole language rule: the browser's choice for anonymous visitors, the account's choice
 * once someone signs in, and the switch in the header. Runtime dictionaries keep the switch instant
 * and let the account language apply without a reload (PRD US-01, US-23).
 */
@Injectable({ providedIn: 'root' })
export class TranslationService {
  private readonly current = signal<Language>(readStoredLanguage());

  readonly language = this.current.asReadonly();

  constructor() {
    // index.html ships with lang="th"; a returning visitor may have chosen otherwise.
    document.documentElement.lang = this.current();
  }

  /** Reading the signal inside makes every template binding that calls this refresh on a switch. */
  t = (key: string): string => TRANSLATIONS[this.current()][key] ?? key;

  use(language: Language): void {
    this.current.set(language);
    document.documentElement.lang = language;
    try {
      localStorage.setItem(LANGUAGE_STORAGE_KEY, language);
    } catch {
      // Private windows and blocked storage are fine; the choice just does not outlive the tab.
    }
  }

  /** The account's language wins over whatever this browser had selected. */
  useAccountLanguage(language: string): void {
    if (isLanguage(language)) {
      this.use(language);
    }
  }
}

function readStoredLanguage(): Language {
  try {
    const stored = localStorage.getItem(LANGUAGE_STORAGE_KEY);
    if (isLanguage(stored)) {
      return stored;
    }
  } catch {
    // Ignore unavailable storage and fall back to the default.
  }
  return DEFAULT_LANGUAGE;
}
