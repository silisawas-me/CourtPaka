import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, inject, OnInit, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { TranslatePipe } from '../../core/i18n/translate.pipe';

interface HealthResponse {
  status: string;
}

@Component({
  selector: 'app-home-page',
  imports: [RouterLink, TranslatePipe],
  templateUrl: './home.page.html',
})
export class HomePage implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);

  protected readonly user = this.auth.currentUser;
  protected readonly apiStatus = signal('checking');

  ngOnInit(): void {
    this.http.get<HealthResponse>('/api/health/ready').subscribe({
      next: (response) => this.apiStatus.set(response.status),
      // The API answers 503 with a health body when the database is down; anything else means it is unreachable.
      error: (error: HttpErrorResponse) =>
        this.apiStatus.set((error.error as HealthResponse | null)?.status ?? 'unreachable'),
    });
  }
}
