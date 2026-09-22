import { computed, Injectable, signal } from '@angular/core';
import {
  DATE_LOCALES,
  DEFAULT_LANGUAGE,
  dictionaryFor,
  isLanguage,
  Language,
  THAI,
} from './locales';

export const LANGUAGE_STORAGE_KEY = 'courtpaka.language';

/**
 * Owns the whole language rule: the browser's choice for anonymous visitors, the account's choice
 * once someone signs in, and the switch in the header. Runtime dictionaries keep the switch instant
 * and let the account language apply without a reload (PRD US-01, US-23).
 */
@Injectable({ providedIn: 'root' })
export class TranslationService {
  private readonly current = signal<Language>(readStoredLanguage());

  /**
   * The words this app has so far: Thai from the start, another language once it is fetched.
   * A signal, so every template that is already on screen rewrites itself when one arrives.
   */
  private readonly words = signal<Partial<Record<Language, Record<string, string>>>>({
    [DEFAULT_LANGUAGE]: THAI,
  });

  readonly language = this.current.asReadonly();

  /** The locale dates are written in, so nothing else has to know how a language maps to one. */
  readonly locale = computed(() => DATE_LOCALES[this.current()]);

  constructor() {
    // index.html ships with lang="th"; a returning visitor may have chosen otherwise.
    document.documentElement.lang = this.current();
  }

  /**
   * Reading the signals inside makes every template binding that calls this refresh on a switch,
   * and again when a language that had to be fetched arrives. Until it does, the words are Thai:
   * the app is readable throughout, which a page of untranslated keys would not be.
   */
  t = (key: string): string => this.words()[this.current()]?.[key] ?? THAI[key] ?? key;

  /** Fetches a language's words if this app does not have them yet. */
  async load(language: Language): Promise<void> {
    if (this.words()[language]) {
      return;
    }

    const words = await dictionaryFor(language);
    this.words.update((have) => ({ ...have, [language]: words }));
  }

  use(language: Language): void {
    void this.load(language);
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

export function readStoredLanguage(): Language {
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
