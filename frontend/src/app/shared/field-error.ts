import { Component, computed, inject, input } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { AbstractControl } from '@angular/forms';
import { switchMap } from 'rxjs';
import { TranslationService } from '../core/i18n/translation.service';

/** One place that decides when a field complains and which message it shows. */
@Component({
  selector: 'app-field-error',
  template: `
    @if (message(); as key) {
      <p class="field-error" [attr.data-testid]="testId()">{{ i18n.t(key) }}</p>
    }
  `,
})
export class FieldError {
  protected readonly i18n = inject(TranslationService);

  readonly control = input.required<AbstractControl>();
  readonly testId = input.required<string>();

  /** Control state is not a signal, so follow its event stream to stay in step with the form. */
  private readonly state = toSignal(
    toObservable(this.control).pipe(switchMap((control) => control.events)),
  );

  protected readonly message = computed(() => {
    this.state();
    const control = this.control();
    if (!control.touched || control.valid) {
      return null;
    }
    if (control.hasError('email')) {
      return 'common.emailInvalid';
    }
    if (control.hasError('minlength')) {
      return 'common.passwordTooShort';
    }
    return 'common.required';
  });
}
