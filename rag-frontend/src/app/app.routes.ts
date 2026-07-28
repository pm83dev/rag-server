import { Routes } from '@angular/router';
import { Ask } from './pages/ask/ask';
import { Upload } from './pages/upload/upload';
import { Documents } from './pages/documents/documents';

export const routes: Routes = [
  { path: '', redirectTo: 'ask', pathMatch: 'full' },
  { path: 'ask', component: Ask },
  { path: 'upload', component: Upload },
  { path: 'documents', component: Documents }
];
