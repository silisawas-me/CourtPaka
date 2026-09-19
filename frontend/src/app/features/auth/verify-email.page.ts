import { Component, inject, OnInit, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ApiError, AuthService } from '../../core/auth/auth.service';
import { TranslatePipe } from '../../core/i18n/translate.pipe';

type VerifyState = 'working' | 'done' | 'failed';

@Component({
  selector: 'app-verify-email-page',
  imports: [RouterLink, TranslatePipe],
  templateUrl: './verify-email.page.html',
})
export class VerifyEmailPage implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);

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
      next: () => this.state.set('done'),
      error: (error: unknown) => {
        if (error instanceof ApiError) {
          this.errorKey.set(`error.${error.code}`);
        }
        this.state.set('failed');
      },
    });
  }
}
