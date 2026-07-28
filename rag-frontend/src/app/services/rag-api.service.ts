import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { AskResponse, DocumentSummary, SearchResult, UploadJob, UploadJobStarted } from '../models/rag.models';

@Injectable({ providedIn: 'root' })
export class RagApiService {
  private readonly baseUrl = environment.apiUrl;

  constructor(private http: HttpClient) {}

  uploadPdf(file: File): Observable<UploadJobStarted> {
    const formData = new FormData();
    formData.append('file', file);
    return this.http.post<UploadJobStarted>(`${this.baseUrl}/api/upload-pdf`, formData);
  }

  getUploadStatus(jobId: string): Observable<UploadJob> {
    return this.http.get<UploadJob>(`${this.baseUrl}/api/upload-status/${jobId}`);
  }

  search(query: string): Observable<SearchResult[]> {
    return this.http.get<SearchResult[]>(`${this.baseUrl}/api/search`, { params: { query } });
  }

  ask(question: string): Observable<AskResponse> {
    return this.http.get<AskResponse>(`${this.baseUrl}/api/ask`, { params: { question } });
  }

  listDocuments(): Observable<DocumentSummary[]> {
    return this.http.get<DocumentSummary[]>(`${this.baseUrl}/api/documents`);
  }

  deleteDocument(source: string): Observable<string> {
    return this.http.delete(`${this.baseUrl}/api/documents/${encodeURIComponent(source)}`, { responseType: 'text' });
  }
}
