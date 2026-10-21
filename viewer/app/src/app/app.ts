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
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MondayApi, ProjectSummary } from './api/monday-api';
import { ChatPanelComponent } from './chat/chat-panel.component';
import { ChatStore } from './chat/chat-store';
import { GraphCanvasComponent } from './graph/graph-canvas.component';
import { GraphEngine } from './graph/graph-engine';
import { Lens } from './graph/graph-screen';
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
 * floating docked left and the node detail docked right. A routed screen (personal tokens, model)
 * takes the workspace's place below the bar while it is open.
 */
@Component({
  selector: 'app-root',
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    GraphCanvasComponent,
    ChatPanelComponent,
    NodePanelComponent,
  ],
  templateUrl: './app.html',
  styleUrl: './app.css',
  host: { '(document:keydown)': 'shortcut($event)' },
})
export class App implements OnInit {
  private readonly api = inject(MondayApi);
  private readonly router = inject(Router);

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

  /** True while a routed screen is open in place of the workspace. */
  protected readonly routed = signal(false);
  /** Kept here, not in the canvas, so coming back from a settings screen keeps the lens. */
  protected readonly lens = signal<Lens>('structure');

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

  /**
   * A routed screen replaces the canvas, and the canvas that comes back draws a fresh graph with
   * nothing selected, so the node panel must not keep showing the old selection.
   */
  protected screenOpened(): void {
    this.routed.set(true);
    this.selectedNode.set(null);
  }

  protected select(event: Event): void {
    const id = (event.target as HTMLSelectElement).value;
    this.selectedId.set(id);
    // The model screen is per project: it follows the picker.
    if (this.router.url.startsWith('/settings/model'))
      void this.router.navigate(['/settings/model'], { queryParams: { project: id } });
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

  /**
   * `/` opens the chat; Esc goes Back on the graph (undoing a screen action). Never while typing,
   * and never while another screen (tokens) replaces the graph.
   */
  protected shortcut(event: KeyboardEvent): void {
    if (
      this.routed() ||
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
