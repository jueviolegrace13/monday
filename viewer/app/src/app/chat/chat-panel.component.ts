import { Component, ElementRef, effect, input, output, signal, viewChild } from '@angular/core';
import { ChatStep, ChatStore } from './chat-store';

/** Where Owners and Admins set the project's model (the settings screen lands in a later task). */
export const modelSettingsUrl = (projectId: string) =>
  `/settings/model?project=${encodeURIComponent(projectId)}`;

/**
 * The chat, floating over the graph, docked left. Answers stream in; each tool step is one mono line
 * that focuses the nodes it touched. Stop ends an answer. Esc in the input closes the panel.
 */
@Component({
  selector: 'app-chat-panel',
  templateUrl: './chat-panel.component.html',
  styleUrl: './chat-panel.component.css',
})
export class ChatPanelComponent {
  readonly store = input.required<ChatStore>();
  readonly projectId = input.required<string>();
  readonly open = input(false);
  /** Debuggers and up: the agent may move the screen. Monitors ask only. */
  readonly canAct = input(false);
  /** Owners and Admins: may set up the model. */
  readonly canManageModel = input(false);
  readonly selectedName = input<string | null>(null);

  readonly toggle = output<void>();
  readonly showNodes = output<string[]>();

  private readonly input = viewChild<ElementRef<HTMLTextAreaElement>>('question');
  private readonly stream = viewChild<ElementRef<HTMLElement>>('stream');

  protected readonly draft = signal('');
  protected readonly settingsUrl = modelSettingsUrl;

  constructor() {
    // Keep the newest words in view while the answer streams.
    effect(() => {
      this.store().messages();
      const el = this.stream()?.nativeElement;
      if (el) queueMicrotask(() => (el.scrollTop = el.scrollHeight));
    });
  }

  focusInput(): void {
    queueMicrotask(() => this.input()?.nativeElement.focus());
  }

  protected stepLine(step: ChatStep): string {
    const n = step.nodeIds.length;
    return `${step.name} · ${n} ${n === 1 ? 'node' : 'nodes'}`;
  }

  protected async submit(event?: Event): Promise<void> {
    event?.preventDefault();
    const text = this.draft();
    if (!text.trim() || this.store().streaming()) return;
    this.draft.set('');
    await this.store().send(text);
  }

  protected keydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      void this.submit();
    } else if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      this.toggle.emit();
    }
  }

  protected typed(event: Event): void {
    this.draft.set((event.target as HTMLTextAreaElement).value);
  }
}
