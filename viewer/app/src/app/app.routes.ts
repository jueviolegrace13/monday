import { Routes } from '@angular/router';

/**
 * The graph workspace is the shell itself (path ''); other screens replace it below the top bar.
 * Unknown paths go back to the graph.
 */
export const routes: Routes = [
  { path: '', pathMatch: 'full', title: 'Monday', children: [] },
  {
    path: 'settings/tokens',
    title: 'Personal tokens · Monday',
    loadComponent: () => import('./settings/tokens.component').then((m) => m.TokensComponent),
  },
  {
    path: 'settings/model',
    title: 'Model · Monday',
    loadComponent: () => import('./settings/model.component').then((m) => m.ModelComponent),
  },
  { path: '**', redirectTo: '' },
];
