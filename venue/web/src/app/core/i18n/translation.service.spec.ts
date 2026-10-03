import { TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../testing/translations';
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

  it('switches language, remembers it and updates the document language', async () => {
    service.use('en');

    // The choice is made at once; the words that go with it are fetched, so they arrive next.
    expect(document.documentElement.lang).toBe('en');
    expect(localStorage.getItem(LANGUAGE_STORAGE_KEY)).toBe('en');

    await service.load('en');
    expect(service.t('login.title')).toBe(TRANSLATIONS.en['login.title']);
  });

  /** Until the other language arrives the screen is Thai, never a page of raw keys. */
  it('reads in Thai while another language is on its way', () => {
    service.use('en');

    expect(service.t('login.title')).toBe(TRANSLATIONS.th['login.title']);
  });

  it('adopts a language an account reports, and ignores one it does not support', () => {
    service.useAccountLanguage('en');
    expect(service.language()).toBe('en');

    service.useAccountLanguage('fr');
    expect(service.language()).toBe('en');
  });

  it('declares the remembered language on the document as soon as it is created', () => {
    localStorage.setItem(LANGUAGE_STORAGE_KEY, 'en');
    document.documentElement.lang = 'th';
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({});

    expect(TestBed.inject(TranslationService).language()).toBe('en');
    expect(document.documentElement.lang).toBe('en');
  });

  it('returns the key itself when a translation is missing', () => {
    expect(service.t('does.not.exist')).toBe('does.not.exist');
  });
});
