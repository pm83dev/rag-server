import { Component, OnInit, signal } from '@angular/core';
import { RagApiService } from '../../services/rag-api.service';
import { DocumentSummary } from '../../models/rag.models';

@Component({
  selector: 'app-documents',
  imports: [],
  templateUrl: './documents.html',
  styleUrl: './documents.scss'
})
export class Documents implements OnInit {
  documents = signal<DocumentSummary[]>([]);
  isLoading = signal(false);
  errorMessage = signal<string | null>(null);
  deletingSource = signal<string | null>(null);

  constructor(private api: RagApiService) {}

  ngOnInit(): void {
    this.refresh();
  }

  refresh(): void {
    this.isLoading.set(true);
    this.errorMessage.set(null);

    this.api.listDocuments().subscribe({
      next: (docs) => {
        this.documents.set(docs);
        this.isLoading.set(false);
      },
      error: (err) => {
        this.errorMessage.set(err?.error ?? 'Errore nel recupero dei documenti.');
        this.isLoading.set(false);
      }
    });
  }

  delete(source: string): void {
    if (!confirm(`Eliminare tutti i chunk del documento "${source}"?`)) {
      return;
    }

    this.deletingSource.set(source);
    this.api.deleteDocument(source).subscribe({
      next: () => {
        this.deletingSource.set(null);
        this.refresh();
      },
      error: (err) => {
        this.errorMessage.set(err?.error ?? 'Errore durante l\'eliminazione.');
        this.deletingSource.set(null);
      }
    });
  }
}
