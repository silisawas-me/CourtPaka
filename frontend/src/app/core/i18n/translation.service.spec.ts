import { TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from './locales';
import { LANGUAGE_STORAGE_KEY, TranslationService } from './translation.service';

describe('TranslationService', () => {
  let service: TranslationService;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({});
    service = TestBed.inject(TranslationService);
  });

  it('starts in Thai', () => {
    expect(service.language()).toBe('th');
    expect(service.t('login.title')).toBe(TRANSLATIONS.th['login.title']);
  });

  it('switches language, remembers it and updates the document language', () => {
    service.use('en');

    expect(service.t('login.title')).toBe(TRANSLATIONS.en['login.title']);
    expect(document.documentElement.lang).toBe('en');
    expect(localStorage.getItem(LANGUAGE_STORAGE_KEY)).toBe('en');
  });

  it('adopts a language an account reports, and ignores one it does not support', () => {
    service.useAccountLanguage('en');
    expect(service.language()).toBe('en');

    service.useAccountLanguage('fr');
    expect(service.language()).toBe('en');
  });

  it('returns the key itself when a translation is missing', () => {
    expect(service.t('does.not.exist')).toBe('does.not.exist');
  });
});
