import { Component, inject, OnInit, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';

type VerifyState = 'working' | 'done' | 'failed';

@Component({
  selector: 'app-verify-email-page',
  imports: [RouterLink],
  templateUrl: './verify-email.page.html',
})
export class VerifyEmailPage implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);

  protected readonly i18n = inject(TranslationService);
  protected readonly state = signal<VerifyState>('working');
  protected readonly errorKey = signal('verify.linkInvalid');

  ngOnInit(): void {
    const parameters = this.route.snapshot.queryParamMap;
    const userId = parameters.get('userId');
    const token = parameters.get('token');

    if (!userId || !token) {
      this.state.set('failed');
      return;
    }

    this.auth.verifyEmail(userId, token).subscribe({
      next: () => {
        this.state.set('done');
        // The account may be signed in here; refresh it so the verified state shows at once.
        this.auth.loadCurrentUser().subscribe({ error: () => undefined });
      },
      error: (error: unknown) => {
        this.errorKey.set(errorKey(error));
        this.state.set('failed');
      },
    });
  }
}
