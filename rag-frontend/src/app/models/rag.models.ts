export interface SearchResult {
  id: string;
  score: number;
  metadata: { [key: string]: unknown };
}

export interface AskSource {
  id: string;
  score: number;
}

export interface AskResponse {
  answer: string;
  sources: AskSource[];
}

export interface DocumentSummary {
  source: string;
  chunks: number;
}

export interface UploadJobStarted {
  jobId: string;
}

export type UploadJobStatus = 'Queued' | 'Processing' | 'Completed' | 'Failed';

export interface UploadJob {
  jobId: string;
  fileName: string;
  status: UploadJobStatus;
  totalChunks: number;
  processedChunks: number;
  errorMessage: string | null;
}
