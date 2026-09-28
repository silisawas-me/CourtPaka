import { ChangeDetectionStrategy, Component, inject, OnInit, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { errorKey } from '../../core/http/api-error';
import { AppDateTimePipe } from '../../core/i18n/app-date.pipe';
import { TranslationService } from '../../core/i18n/translation.service';
import { AdminVenuesService, OwnerInvitation } from '../../core/venues/admin-venues.service';

/**
 * The platform inviting somebody to bring their venue on (docs/plan/owner-complete.md 3a). With
 * sign-up closed this is how a new owner gets an account: the address, the language to write in,
 * and a list of who was asked and whether they came.
 */
@Component({
  selector: 'app-invite-owner',
  imports: [MatButtonModule, MatCardModule, AppDateTimePipe],
  templateUrl: './invite-owner.html',
  styleUrl: './invite-owner.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class InviteOwner implements OnInit {
  private readonly admin = inject(AdminVenuesService);
  protected readonly i18n = inject(TranslationService);

  protected readonly languages = ['th', 'en'] as const;
  protected readonly email = signal('');
  protected readonly language = signal<'th' | 'en'>('th');
  protected readonly sent = signal<OwnerInvitation[]>([]);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly done = signal<string | null>(null);

  ngOnInit(): void {
    this.admin.ownerInvitations().subscribe({
      next: (all) => this.sent.set(all),
      error: (failure: unknown) => this.error.set(errorKey(failure)),
    });
  }

  protected standing(one: OwnerInvitation): 'accepted' | 'expired' | 'waiting' {
    return one.acceptedAt
      ? 'accepted'
      : new Date(one.expiresAt) < new Date()
        ? 'expired'
        : 'waiting';
  }

  protected invite(): void {
    const email = this.email().trim();
    if (email === '' || this.busy()) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    this.done.set(null);
    this.admin.inviteOwner(email, this.language()).subscribe({
      next: (invited) => {
        this.busy.set(false);
        this.done.set(invited.email);
        this.email.set('');
        this.sent.update((all) => [invited, ...all.filter((one) => one.email !== invited.email)]);
      },
      error: (failure: unknown) => {
        this.busy.set(false);
        this.error.set(errorKey(failure));
      },
    });
  }
}
