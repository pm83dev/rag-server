import { Injectable, computed, signal } from '@angular/core';
import { RagApiService } from './rag-api.service';
import { ToastService } from './toast.service';
import { UploadJob } from '../models/rag.models';

const POLL_INTERVAL_MS = 2000;

@Injectable({ providedIn: 'root' })
export class UploadJobsService {
  private jobsMap = signal<Map<string, UploadJob>>(new Map());
  private pollHandles = new Map<string, ReturnType<typeof setInterval>>();

  // Più recenti per primi. Essendo un servizio a livello di app (non di componente),
  // il tracking sopravvive alla navigazione tra pagine: il polling continua anche se
  // l'utente lascia la pagina "Carica PDF".
  jobs = computed(() => Array.from(this.jobsMap().values()).reverse());

  constructor(private api: RagApiService, private toast: ToastService) {}

  startUpload(file: File): void {
    this.api.uploadPdf(file).subscribe({
      next: ({ jobId }) => {
        this.setJob({
          jobId,
          fileName: file.name,
          status: 'Queued',
          totalChunks: 0,
          processedChunks: 0,
          errorMessage: null
        });
        this.poll(jobId);
      },
      error: (err) => {
        this.toast.show({
          type: 'danger',
          message: `Errore nel caricamento di '${file.name}': ${err?.error ?? 'errore sconosciuto'}`
        });
      }
    });
  }

  private setJob(job: UploadJob): void {
    const map = new Map(this.jobsMap());
    map.set(job.jobId, job);
    this.jobsMap.set(map);
  }

  private poll(jobId: string): void {
    const handle = setInterval(() => {
      this.api.getUploadStatus(jobId).subscribe({
        next: (job) => {
          this.setJob(job);

          if (job.status === 'Completed') {
            this.stopPoll(jobId);
            this.toast.show({
              type: 'success',
              message: `Indicizzati ${job.totalChunks} chunk da '${job.fileName}'.`
            });
          } else if (job.status === 'Failed') {
            this.stopPoll(jobId);
            this.toast.show({
              type: 'danger',
              message: `Errore nell'indicizzazione di '${job.fileName}': ${job.errorMessage}`
            });
          }
        },
        error: () => this.stopPoll(jobId)
      });
    }, POLL_INTERVAL_MS);

    this.pollHandles.set(jobId, handle);
  }

  private stopPoll(jobId: string): void {
    const handle = this.pollHandles.get(jobId);
    if (handle !== undefined) {
      clearInterval(handle);
      this.pollHandles.delete(jobId);
    }
  }
}
