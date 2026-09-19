import { Component, computed, inject, input } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { AbstractControl } from '@angular/forms';
import { switchMap } from 'rxjs';
import { TranslationService } from '../core/i18n/translation.service';

/**
 * One place that decides which message a field shows. It sits on a `mat-error`, which is what the
 * form field looks for when it decides where to put the message and when to show it: written as
 * anything else, the message is projected next to the input instead of under the field.
 */
@Component({
  selector: 'mat-error[appFieldError]',
  host: { '[attr.data-testid]': 'testId()' },
  template: `{{ i18n.t(message()) }}`,
})
export class FieldError {
  protected readonly i18n = inject(TranslationService);

  readonly control = input.required<AbstractControl>({ alias: 'appFieldError' });
  readonly testId = input.required<string>();

  /** Control state is not a signal, so follow its event stream to stay in step with the form. */
  private readonly state = toSignal(
    toObservable(this.control).pipe(switchMap((control) => control.events)),
  );

  /**
   * Which rule the field broke. The form field decides whether this is on screen at all — it shows
   * errors once the control is invalid and the user has touched it or submitted the form.
   */
  protected readonly message = computed(() => {
    this.state();
    const control = this.control();
    if (control.hasError('email')) {
      return 'common.emailInvalid';
    }
    if (control.hasError('minlength')) {
      return 'common.passwordTooShort';
    }
    return 'common.required';
  });
}
