import { Component, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RagApiService } from '../../services/rag-api.service';
import { AskResponse } from '../../models/rag.models';

@Component({
  selector: 'app-ask',
  imports: [FormsModule],
  templateUrl: './ask.html',
  styleUrl: './ask.scss'
})
export class Ask {
  question = signal('');
  isLoading = signal(false);
  response = signal<AskResponse | null>(null);
  errorMessage = signal<string | null>(null);

  constructor(private api: RagApiService) {}

  displayId(id: string): string {
    const match = id.match(/[0-9a-f-]{36}/i);
    return match ? match[0] : id;
  }

  reset(): void {
    this.question.set('');
    this.response.set(null);
    this.errorMessage.set(null);
  }

  ask(): void {
    const q = this.question().trim();
    if (!q) {
      return;
    }

    this.isLoading.set(true);
    this.response.set(null);
    this.errorMessage.set(null);

    this.api.ask(q).subscribe({
      next: (res) => {
        this.response.set(res);
        this.isLoading.set(false);
      },
      error: (err) => {
        this.errorMessage.set(err?.error ?? 'Errore durante la richiesta.');
        this.isLoading.set(false);
      }
    });
  }
}
