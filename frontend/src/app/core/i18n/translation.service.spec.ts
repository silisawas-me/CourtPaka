import { TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from './locales';
import { TranslationService } from './translation.service';

describe('TranslationService', () => {
  let service: TranslationService;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({});
    service = TestBed.inject(TranslationService);
  });

  it('starts in Thai', () => {
    expect(service.language()).toBe('th');
    expect(service.translate('login.title')).toBe(TRANSLATIONS.th['login.title']);
  });

  it('switches language, remembers it and updates the document language', () => {
    service.use('en');

    expect(service.translate('login.title')).toBe(TRANSLATIONS.en['login.title']);
    expect(document.documentElement.lang).toBe('en');
    expect(localStorage.getItem('courtpaka.language')).toBe('en');
  });

  it('returns the key itself when a translation is missing', () => {
    expect(service.translate('does.not.exist')).toBe('does.not.exist');
  });
});
