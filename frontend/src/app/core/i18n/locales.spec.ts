import { LANGUAGES } from './locales';
import { TRANSLATIONS } from '../../testing/translations';

describe('translations', () => {
  const [source, ...others] = LANGUAGES;
  const sourceKeys = Object.keys(TRANSLATIONS[source]).sort();

  it('has at least one key', () => {
    expect(sourceKeys.length).toBeGreaterThan(0);
  });

  for (const language of others) {
    it(`has exactly the same keys in ${language} as in ${source}`, () => {
      expect(Object.keys(TRANSLATIONS[language]).sort()).toEqual(sourceKeys);
    });

    it(`has no empty value in ${language}`, () => {
      const empty = Object.entries(TRANSLATIONS[language])
        .filter(([, value]) => value.trim() === '')
        .map(([key]) => key);
      expect(empty).toEqual([]);
    });
  }

  it('translates every API error code the UI can receive', () => {
    const apiErrorCodes = [
      'auth.invalid_credentials',
      'auth.invalid_verification_token',
      'auth.unsupported_language',
      'auth.weak_password',
      'auth.invalid_email',
      'auth.privacy_policy_outdated',
      'auth.registration_failed',
      'tooManyRequests',
      'sessionNotEstablished',
      'unknown',
    ];

    const missing = apiErrorCodes.filter((code) => !(`error.${code}` in TRANSLATIONS[source]));
    expect(missing).toEqual([]);
  });
});
