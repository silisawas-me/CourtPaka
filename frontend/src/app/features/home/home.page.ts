import { Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { TranslationService } from '../../core/i18n/translation.service';

@Component({
  selector: 'app-home-page',
  imports: [RouterLink],
  templateUrl: './home.page.html',
})
export class HomePage {
  private readonly auth = inject(AuthService);

  protected readonly i18n = inject(TranslationService);
  protected readonly user = this.auth.currentUser;
}
