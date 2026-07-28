import { Component, signal } from '@angular/core';
import { UploadJobsService } from '../../services/upload-jobs.service';
import { UploadJob } from '../../models/rag.models';

@Component({
  selector: 'app-upload',
  imports: [],
  templateUrl: './upload.html',
  styleUrl: './upload.scss'
})
export class Upload {
  selectedFile = signal<File | null>(null);

  constructor(public jobsService: UploadJobsService) {}

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.selectedFile.set(input.files?.[0] ?? null);
  }

  upload(): void {
    const file = this.selectedFile();
    if (!file) {
      return;
    }
    this.jobsService.startUpload(file);
    this.selectedFile.set(null);
  }

  progressPercent(job: UploadJob): number {
    if (job.totalChunks === 0) {
      return job.status === 'Completed' ? 100 : 0;
    }
    return Math.round((job.processedChunks / job.totalChunks) * 100);
  }
}
