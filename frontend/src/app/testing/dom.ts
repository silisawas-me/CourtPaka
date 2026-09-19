import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { EnvironmentProviders, Provider } from '@angular/core';
import { ComponentFixture } from '@angular/core/testing';
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
