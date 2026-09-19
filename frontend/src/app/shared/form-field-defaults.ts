import { Provider } from '@angular/core';
import { MAT_FORM_FIELD_DEFAULT_OPTIONS } from '@angular/material/form-field';

/**
 * How every field in the app looks, decided once. Pages carry it rather than the app, because the
 * token lives in the form-field package and the shell has no other reason to load it.
 *
 * `dynamic` is what Material offers instead of reserving a line under every field for a hint that
 * is not there — overriding its subscript styles by hand reaches into generated class names.
 */
export const FORM_FIELD_DEFAULTS: Provider = {
  provide: MAT_FORM_FIELD_DEFAULT_OPTIONS,
  useValue: { appearance: 'outline', subscriptSizing: 'dynamic' },
};
