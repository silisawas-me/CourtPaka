import { LANGUAGES } from './locales';
import { TRANSLATIONS } from '../../testing/translations';

// The bundler provides this and ships no declaration for it.
declare global {
  interface ImportMeta {
    glob(pattern: string, options: object): Record<string, string>;
  }
}

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

  /**
   * Every key a template asks for has to exist, or the screen shows the key itself. That is not
   * a crash and no other test notices it — it reaches a person as the words `common.save` on a
   * button. Found exactly that way once, on a screenshot, which is a slow way to find it.
   *
   * Only the literal calls can be checked. A key built at runtime — `'venues.status.' + status`
   * — is checked by the spec of the screen that builds it.
   */
  it('has every key the screens ask for by name', async () => {
    // Written out in full: the bundler replaces this exact call at build time and will not
    // follow it through a variable.
    const templates = import.meta.glob('/src/app/**/*.html', {
      query: '?raw',
      import: 'default',
      eager: true,
    }) as Record<string, string>;

    const asked = new Set<string>();
    for (const markup of Object.values(templates)) {
      for (const [, key] of markup.matchAll(/i18n\.t\(\s*'([^']+)'\s*\)/g)) {
        asked.add(key);
      }
    }

    expect(asked.size).toBeGreaterThan(0);
    expect([...asked].filter((key) => !(key in TRANSLATIONS[source])).sort()).toEqual([]);
  });
});
