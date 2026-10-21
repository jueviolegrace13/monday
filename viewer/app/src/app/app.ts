import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { MondayApi, ProjectSummary } from './api/monday-api';

type Load = 'loading' | 'ready' | 'failed';

/** The shell: a top bar with the project picker, and the stage the graph and chat panel will fill. */
@Component({
  selector: 'app-root',
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  private readonly api = inject(MondayApi);

  protected readonly load = signal<Load>('loading');
  protected readonly error = signal('');
  protected readonly projects = signal<ProjectSummary[]>([]);
  protected readonly selectedId = signal('');
  protected readonly selected = computed(
    () => this.projects().find((p) => p.id === this.selectedId()) ?? null,
  );

  async ngOnInit(): Promise<void> {
    await this.reload();
  }

  protected async reload(): Promise<void> {
    this.load.set('loading');
    try {
      const projects = await this.api.projects();
      this.projects.set(projects);
      if (!projects.some((p) => p.id === this.selectedId()))
        this.selectedId.set(projects[0]?.id ?? '');
      this.load.set('ready');
    } catch (e) {
      this.error.set(e instanceof Error ? e.message : 'Could not load projects.');
      this.load.set('failed');
    }
  }

  protected select(event: Event): void {
    this.selectedId.set((event.target as HTMLSelectElement).value);
  }
}
