import {
  Component,
  OnInit,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { MondayApi, ProjectSummary } from './api/monday-api';
import { ChatPanelComponent } from './chat/chat-panel.component';
import { ChatStore } from './chat/chat-store';
import { GraphCanvasComponent } from './graph/graph-canvas.component';
import { GraphEngine } from './graph/graph-engine';
import { GraphScreenRef } from './graph/graph-screen-ref';
import { NodePanelComponent } from './node/node-panel.component';

type Load = 'loading' | 'ready' | 'failed';

/** True while focus is somewhere the person types, so shortcuts stay out of the way. */
const isTyping = (target: EventTarget | null): boolean =>
  target instanceof HTMLElement &&
  (target.isContentEditable ||
    !!target.closest('input, textarea, select, [contenteditable="true"]'));

/**
 * The shell: a top bar with the project picker, and the workspace: the code graph, with the chat
 * floating docked left and the node detail docked right.
 */
@Component({
  selector: 'app-root',
  imports: [GraphCanvasComponent, ChatPanelComponent, NodePanelComponent],
  templateUrl: './app.html',
  styleUrl: './app.css',
  host: { '(document:keydown)': 'shortcut($event)' },
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
  protected readonly canAct = computed(
    () => !!this.selected() && this.selected()!.role !== 'Monitor',
  );
  protected readonly canManageModel = computed(() =>
    ['Owner', 'Admin'].includes(this.selected()?.role ?? ''),
  );

  protected readonly chatOpen = signal(false);
  protected readonly nodeOpen = signal(true);
  protected readonly selectedNode = signal<string | null>(null);
  protected readonly selectedName = computed(() => {
    const id = this.selectedNode();
    const n = id ? this.engine()?.graph?.byId.get(id) : undefined;
    return n ? (n.kind === 'Endpoint' && n.route ? n.route : n.name) : null;
  });

  private readonly engine = signal<GraphEngine | null>(null);
  private readonly screen = new GraphScreenRef();
  protected readonly chat = new ChatStore(this.api, this.screen, {
    projectId: () => this.selectedId(),
    selectedNodeId: () => this.selectedNode(),
    canAct: () => this.canAct(),
  });

  private readonly chatPanel = viewChild(ChatPanelComponent);

  constructor() {
    // Another project is another conversation and another graph.
    effect(() => {
      this.selectedId();
      untracked(() => {
        this.chat.reset();
        this.selectedNode.set(null);
        this.screen.engine = null;
        this.engine.set(null);
      });
    });
  }

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

  protected engineReady(engine: GraphEngine | null): void {
    this.screen.engine = engine;
    this.engine.set(engine);
  }

  protected toggleChat(): void {
    const open = !this.chatOpen();
    this.chatOpen.set(open);
    if (open) setTimeout(() => this.chatPanel()?.focusInput());
  }

  /** A tool step's line: one node is focused, several are lit as a path. */
  protected showNodes(ids: string[]): void {
    if (ids.length === 1) this.screen.focus(ids[0]);
    else if (ids.length > 1) this.screen.highlightPath(ids);
  }

  protected focusNode(id: string): void {
    this.engine()?.focus(id);
  }

  protected selectNode(id: string): void {
    this.engine()?.select(id);
  }

  /** `/` opens the chat; Esc goes Back on the graph (undoing a screen action). Never while typing. */
  protected shortcut(event: KeyboardEvent): void {
    if (
      event.defaultPrevented ||
      event.ctrlKey ||
      event.metaKey ||
      event.altKey ||
      isTyping(event.target)
    )
      return;
    if (event.key === '/' && this.selected()) {
      event.preventDefault();
      this.chatOpen.set(true);
      // The panel renders on the next change detection; focus after it.
      setTimeout(() => this.chatPanel()?.focusInput());
    } else if (event.key === 'Escape') {
      const engine = this.engine();
      if (engine) {
        event.preventDefault();
        engine.back();
      }
    }
  }
}
