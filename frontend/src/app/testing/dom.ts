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

export function check(fixture: ComponentFixture<unknown>, selector: string): void {
  (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(selector)!.click();
  fixture.detectChanges();
}

export function submitForm(fixture: ComponentFixture<unknown>): void {
  (fixture.nativeElement as HTMLElement).querySelector('form')!.dispatchEvent(new Event('submit'));
  fixture.detectChanges();
}
