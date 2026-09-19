import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, inject, OnInit, signal } from '@angular/core';
import { RouterOutlet } from '@angular/router';

interface HealthResponse {
  status: string;
}

@Component({
  imports: [RouterOutlet],
  selector: 'app-root',
  styleUrl: './app.scss',
  templateUrl: './app.html',
})
export class App implements OnInit {
  private readonly http = inject(HttpClient);

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
