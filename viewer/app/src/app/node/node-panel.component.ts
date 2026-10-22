import { Component, effect, inject, input, output, signal, untracked } from '@angular/core';
import { CodeNode, MondayApi, NodeDetail } from '../api/monday-api';
import { isOpenGuard } from '../graph/graph-model';

type Load = 'idle' | 'loading' | 'ready' | 'failed';

/**
 * The selected node, docked right over the graph: what it is, where it lives, its guards, the
 * explanation and code (when the person may see code), and who calls it. A related node is one
 * click away. Everything comes from GET /projects/{id}/graph/node, so role rules apply.
 */
@Component({
  selector: 'app-node-panel',
  templateUrl: './node-panel.component.html',
  styleUrl: './node-panel.component.css',
})
export class NodePanelComponent {
  private readonly api = inject(MondayApi);

  readonly projectId = input.required<string>();
  readonly nodeId = input<string | null>(null);
  readonly open = input(true);

  readonly focusNode = output<string>();
  readonly selectNode = output<string>();
  readonly toggle = output<void>();

  protected readonly load = signal<Load>('idle');
  protected readonly detail = signal<NodeDetail | null>(null);
  protected readonly error = signal('');
  private abort: AbortController | null = null;

  constructor() {
    effect(() => {
      const project = this.projectId();
      const node = this.nodeId();
      untracked(() => void this.fetch(project, node));
    });
  }

  protected relations(d: NodeDetail): { title: string; nodes: CodeNode[] }[] {
    return [
      { title: 'Called by', nodes: d.callers },
      { title: 'Calls', nodes: d.callees },
      { title: 'Tests that cover it', nodes: d.tests },
      { title: 'Endpoints that reach it', nodes: d.endpoints },
    ].filter((r) => r.nodes.length > 0);
  }

  protected label(n: CodeNode): string {
    return n.kind === 'Endpoint' && n.route ? n.route : n.name;
  }

  protected location(n: CodeNode): string {
    if (!n.file) return n.id;
    return n.startLine ? `${n.file}:${n.startLine}–${n.endLine ?? n.startLine}` : n.file;
  }

  protected readonly isOpenGuard = isOpenGuard;

  private async fetch(projectId: string, nodeId: string | null): Promise<void> {
    this.abort?.abort();
    this.detail.set(null);
    if (!nodeId) {
      this.load.set('idle');
      return;
    }
    const abort = new AbortController();
    this.abort = abort;
    this.load.set('loading');
    try {
      const detail = await this.api.node(projectId, nodeId, abort.signal);
      if (abort.signal.aborted) return;
      this.detail.set(detail);
      this.load.set('ready');
    } catch (e) {
      if (abort.signal.aborted) return;
      this.error.set(e instanceof Error ? e.message : 'The node could not be loaded.');
      this.load.set('failed');
    }
  }
}
