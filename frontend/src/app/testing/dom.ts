import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { EnvironmentProviders, Provider } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AuthService } from '../core/auth/auth.service';
import { provideRouter, Routes } from '@angular/router';
import { apiErrorInterceptor } from '../core/http/api-error';

/**
 * The same HTTP + router wiring the app uses, so specs exercise the real interceptor.
 * Pass the routes a page navigates to; without them the navigation rejects in the background.
 */
export function pageProviders(routes: Routes = []): (Provider | EnvironmentProviders)[] {
  return [
    provideHttpClient(withInterceptors([apiErrorInterceptor])),
    provideHttpClientTesting(),
    provideRouter(routes),
  ];
}

/** Puts a signed-in account behind the pages under test, the way the app initializer does. */
export function signInAs(email = 'user@example.com'): void {
  TestBed.inject(AuthService).loadCurrentUser().subscribe();
  TestBed.inject(HttpTestingController)
    .expectOne('/api/auth/me')
    .flush({ id: 'u0', email, emailConfirmed: true, language: 'th' });
}

export function textOf(fixture: ComponentFixture<unknown>, testId: string): string | undefined {
  const element = fixture.nativeElement as HTMLElement;
  return element.querySelector(`[data-testid="${testId}"]`)?.textContent?.trim();
}

export function setInput(
  fixture: ComponentFixture<unknown>,
  selector: string,
  value: string,
): void {
  const input = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(selector)!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  fixture.detectChanges();
}

/** Clicks whatever the selector names: a button, or the control inside a Material checkbox. */
export function check(fixture: ComponentFixture<unknown>, selector: string): void {
  controlOf(fixture, selector).click();
  fixture.detectChanges();
}

/**
 * A Material control carries the test id on its host and keeps the thing you actually click
 * inside it: a checkbox has an <input>, a slide toggle and a button toggle have a <button>.
 * Anything with neither — a plain button or link — is returned as it is.
 */
export function controlOf(fixture: ComponentFixture<unknown>, selector: string): HTMLElement {
  const element = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>(selector)!;
  // An input has no children, so it falls through to itself; a button or link does too.
  return element.querySelector<HTMLElement>('input, button') ?? element;
}

/**
 * Whether a control a test id names is on, read the same way whichever shape Material gave it: a
 * checkbox reports `checked` on its input, a slide toggle reports `aria-checked` on its button.
 */
export function isOn(fixture: ComponentFixture<unknown>, testId: string): boolean {
  const element = controlOf(fixture, `[data-testid="${testId}"]`);
  return element instanceof HTMLInputElement
    ? element.checked
    : element.getAttribute('aria-checked') === 'true';
}

/** Whether it refuses to be used. Anything that is neither an input nor a button cannot say. */
export function isDisabled(fixture: ComponentFixture<unknown>, testId: string): boolean {
  const element = controlOf(fixture, `[data-testid="${testId}"]`);
  if (element instanceof HTMLInputElement || element instanceof HTMLButtonElement) {
    return element.disabled;
  }
  throw new Error(`[data-testid="${testId}"] holds no control that can be disabled`);
}

/** Submits the page's only form, or the one the selector names when a page has several. */
export function submitForm(fixture: ComponentFixture<unknown>, selector = 'form'): void {
  (fixture.nativeElement as HTMLElement)
    .querySelector(selector)!
    .dispatchEvent(new Event('submit'));
  fixture.detectChanges();
}

/**
 * Presses what a data-testid names. The same traversal as check(): Material puts the test id on
 * the host and the control inside it, and so does the availability grid.
 */
export function clickOn(fixture: ComponentFixture<unknown>, testId: string): void {
  check(fixture, `[data-testid="${testId}"]`);
}

/** The element a data-testid names, typed as whatever the caller needs to read off it. */
export function elementOf<T extends Element>(
  fixture: ComponentFixture<unknown>,
  testId: string,
): T | null {
  return (fixture.nativeElement as HTMLElement).querySelector<T>(`[data-testid="${testId}"]`);
}
