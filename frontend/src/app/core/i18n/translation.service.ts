import { Injectable, computed, signal } from '@angular/core';
import { DEFAULT_LANGUAGE, isLanguage, Language, TRANSLATIONS } from './locales';

const STORAGE_KEY = 'courtpaka.language';

/**
 * Runtime translations so the language can change without reloading the app, and so the language
 * stored on the account (US-01) can be applied as soon as the user signs in.
 */
@Injectable({ providedIn: 'root' })
export class TranslationService {
  private readonly current = signal<Language>(readStoredLanguage());

  readonly language = this.current.asReadonly();
  readonly dictionary = computed(() => TRANSLATIONS[this.current()]);

  use(language: Language): void {
    this.current.set(language);
    document.documentElement.lang = language;
    try {
      localStorage.setItem(STORAGE_KEY, language);
    } catch {
      // Private windows and blocked storage are fine; the choice just does not outlive the tab.
    }
  }

  translate(key: string): string {
    return this.dictionary()[key] ?? key;
  }
}

function readStoredLanguage(): Language {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    if (isLanguage(stored)) {
      return stored;
    }
  } catch {
    // Ignore unavailable storage and fall back to the default.
  }
  return DEFAULT_LANGUAGE;
}
