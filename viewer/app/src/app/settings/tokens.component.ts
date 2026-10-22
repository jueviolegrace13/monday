import { DatePipe, DOCUMENT } from '@angular/common';
import {
  Component,
  ElementRef,
  Injector,
  OnDestroy,
  OnInit,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { MondayApi, PersonalTokenView, ProjectSummary, TokenScope } from '../api/monday-api';

type Load = 'loading' | 'ready' | 'failed';
type Reach = 'all' | 'some';

/** The lifetime range the API allows: 90 days by default, a year at most. */
const DEFAULT_DAYS = 90;
const MAX_DAYS = 365;
const DAY_MS = 24 * 60 * 60 * 1000;

/** A token that was just created: the only place its plain text ever lives in the viewer. */
interface Issued {
  plain: string;
  command: string;
}

const message = (e: unknown, fallback: string) => (e instanceof Error ? e.message : fallback);

/**
 * The signed-in person's personal access tokens, for outside agents (Claude Code, Cursor) to call
 * Monday's MCP endpoint as them. Lists, creates and revokes. A new token's plain text is shown once,
 * kept only in this component while it is on screen, and dropped on Done or on leaving the page; the
 * API never returns it again.
 */
@Component({
  selector: 'app-tokens',
  imports: [RouterLink, DatePipe],
  templateUrl: './tokens.component.html',
  styleUrl: './tokens.component.css',
})
export class TokensComponent implements OnInit, OnDestroy {
  private readonly api = inject(MondayApi);
  private readonly origin = inject(DOCUMENT).location.origin;
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  protected readonly maxDays = MAX_DAYS;

  protected readonly load = signal<Load>('loading');
  protected readonly loadError = signal('');
  protected readonly tokens = signal<PersonalTokenView[]>([]);
  protected readonly projects = signal<ProjectSummary[]>([]);

  protected readonly name = signal('');
  protected readonly scope = signal<TokenScope>('read');
  protected readonly reach = signal<Reach>('all');
  protected readonly picked = signal<ReadonlySet<string>>(new Set());
  protected readonly days = signal(String(DEFAULT_DAYS));
  protected readonly creating = signal(false);
  protected readonly createError = signal('');

  protected readonly issued = signal<Issued | null>(null);
  protected readonly copied = signal('');
  protected readonly copyError = signal('');

  protected readonly confirming = signal<string | null>(null);
  protected readonly revoking = signal(false);
  protected readonly revokeError = signal('');

  protected readonly daysValue = computed(() => {
    const n = Number(this.days());
    return Number.isInteger(n) && n >= 1 && n <= MAX_DAYS ? n : null;
  });
  protected readonly expiresOn = computed(() => {
    const n = this.daysValue();
    return n === null ? null : new Date(Date.now() + n * DAY_MS);
  });
  protected readonly canCreate = computed(
    () =>
      !this.creating() &&
      !!this.name().trim() &&
      this.daysValue() !== null &&
      (this.reach() === 'all' || this.picked().size > 0),
  );

  async ngOnInit(): Promise<void> {
    // The project limits are optional: without the list, a token can still cover every project.
    void this.api.projects().then(
      (p) => this.projects.set(p),
      () => this.projects.set([]),
    );
    await this.refresh();
  }

  ngOnDestroy(): void {
    this.issued.set(null);
  }

  protected async refresh(): Promise<void> {
    try {
      this.tokens.set(await this.api.tokens());
      this.load.set('ready');
    } catch (e) {
      this.loadError.set(message(e, 'Could not load your tokens.'));
      this.load.set('failed');
    }
  }

  protected typed(field: 'name' | 'days', event: Event): void {
    this[field].set((event.target as HTMLInputElement).value);
  }

  protected setScope(scope: TokenScope): void {
    this.scope.set(scope);
  }

  protected setReach(reach: Reach): void {
    this.reach.set(reach);
  }

  protected togglePick(id: string, event: Event): void {
    const on = (event.target as HTMLInputElement).checked;
    const next = new Set(this.picked());
    if (on) next.add(id);
    else next.delete(id);
    this.picked.set(next);
  }

  protected async create(event: Event): Promise<void> {
    event.preventDefault();
    if (!this.canCreate()) return;
    this.creating.set(true);
    this.createError.set('');
    this.dismiss();
    try {
      const picked = this.projects()
        .map((p) => p.id)
        .filter((id) => this.picked().has(id));
      const { token, plain } = await this.api.createToken({
        name: this.name().trim(),
        scope: this.scope(),
        projects: this.reach() === 'some' ? picked : null,
        lifetimeDays: this.daysValue(),
      });
      this.tokens.update((list) => [token, ...list.filter((t) => t.id !== token.id)]);
      this.issued.set({ plain, command: this.connectCommand(plain) });
      this.name.set('');
      // Move to the one-time card so it is announced, and so the token is the next thing read.
      this.focusAfterRender('#issued-title');
    } catch (e) {
      this.createError.set(message(e, 'Could not create the token.'));
    } finally {
      this.creating.set(false);
    }
  }

  /** Forgets the plain token. Nothing can show it again. */
  protected dismiss(): void {
    this.issued.set(null);
    this.copied.set('');
    this.copyError.set('');
  }

  protected async copy(what: string, text: string): Promise<void> {
    this.copied.set('');
    this.copyError.set('');
    try {
      await navigator.clipboard.writeText(text);
      this.copied.set(what);
    } catch {
      this.copyError.set('Copying is blocked here. Select the text and copy it by hand.');
    }
  }

  protected askRevoke(id: string): void {
    this.revokeError.set('');
    this.confirming.set(id);
    this.focusAfterRender('[data-confirm]');
  }

  protected cancelRevoke(id: string): void {
    this.confirming.set(null);
    this.focusBack(id);
  }

  protected async revoke(token: PersonalTokenView): Promise<void> {
    this.revoking.set(true);
    this.revokeError.set('');
    try {
      await this.api.revokeToken(token.id);
    } catch (e) {
      this.revokeError.set(message(e, 'Could not revoke the token.'));
    } finally {
      // Either way, show what the server now holds (it may have been revoked elsewhere).
      this.confirming.set(null);
      await this.refresh();
      this.revoking.set(false);
      this.focusBack(token.id);
    }
  }

  protected scopeLabel(scope: TokenScope): string {
    return scope === 'read_propose' ? 'read + propose' : 'read';
  }

  protected projectsLabel(ids: string[] | null): string {
    if (!ids) return 'All projects';
    const names = new Map(this.projects().map((p) => [p.id, p.name]));
    return ids.map((id) => names.get(id) ?? id.slice(0, 8)).join(', ');
  }

  protected state(token: PersonalTokenView): 'Active' | 'Revoked' | 'Expired' {
    return token.revokedAt ? 'Revoked' : token.active ? 'Active' : 'Expired';
  }

  /** Back to the row's Revoke button when it is still there, otherwise to the list's heading. */
  private focusBack(id: string): void {
    this.focusAfterRender(`[data-revoke="${id}"]`, '#list-title');
  }

  private focusAfterRender(...selectors: string[]): void {
    afterNextRender(
      () => {
        for (const selector of selectors) {
          const el = this.host.nativeElement.querySelector<HTMLElement>(selector);
          if (el) return el.focus();
        }
      },
      { injector: this.injector },
    );
  }

  private connectCommand(plain: string): string {
    return `claude mcp add --transport http monday ${this.origin}/mcp --header "Authorization: Bearer ${plain}"`;
  }
}
