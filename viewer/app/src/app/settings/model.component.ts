import { DatePipe, DecimalPipe } from '@angular/common';
import {
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  ModelProvider,
  ModelSettingsView,
  ModelTestResult,
  ModelUsageView,
  MondayApi,
  ProjectSummary,
} from '../api/monday-api';

type Load = 'loading' | 'ready' | 'failed' | 'no-project' | 'not-allowed';
type Field = 'modelName' | 'baseUrl' | 'key' | 'cap';

const PROVIDERS: { value: ModelProvider; label: string }[] = [
  { value: 'Anthropic', label: 'Anthropic' },
  { value: 'OpenAiCompatible', label: 'OpenAI-compatible' },
  { value: 'Gemini', label: 'Gemini' },
];

/** The server refuses shorter keys, so the last 4 shown never give most of a key away. */
const MIN_KEY_LENGTH = 8;
const MANAGERS: readonly string[] = ['Owner', 'Admin'];

const message = (e: unknown, fallback: string) => (e instanceof Error ? e.message : fallback);

/** Two base URLs are the same when they parse to the same address (the server compares Uris). */
const sameUrl = (a: string | null | undefined, b: string | null | undefined): boolean => {
  if (a == null || b == null) return a == b;
  try {
    return new URL(a).href === new URL(b).href;
  } catch {
    return a === b;
  }
};

/**
 * A project's model: provider, model name, base URL (OpenAI-compatible only), key and monthly cap,
 * with "test connection" and this month's usage. Owners and Admins only; the API enforces it too.
 * The key is write-only: the field is never filled from the server, which only sends the last 4.
 * Left empty, the saved key is kept, unless the provider or base URL changed: a saved key only goes
 * where it was set to go, so the server asks for it again, and so does this screen.
 */
@Component({
  selector: 'app-model',
  imports: [RouterLink, DatePipe, DecimalPipe],
  templateUrl: './model.component.html',
  styleUrl: './model.component.css',
})
export class ModelComponent {
  private readonly api = inject(MondayApi);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  /** From `?project=` (router input binding). */
  readonly project = input<string>('');

  protected readonly providers = PROVIDERS;
  protected readonly minKeyLength = MIN_KEY_LENGTH;

  protected readonly load = signal<Load>('loading');
  protected readonly loadError = signal('');
  protected readonly summary = signal<ProjectSummary | null>(null);
  protected readonly saved = signal<ModelSettingsView | null>(null);
  protected readonly usage = signal<ModelUsageView | null>(null);

  protected readonly provider = signal<ModelProvider>('Anthropic');
  protected readonly modelName = signal('');
  protected readonly baseUrl = signal('');
  /** Only what the person types. Never set from the server. */
  protected readonly key = signal('');
  protected readonly cap = signal('');

  protected readonly saving = signal(false);
  protected readonly saveError = signal('');
  protected readonly savedNote = signal('');

  protected readonly testing = signal(false);
  protected readonly testResult = signal<ModelTestResult | null>(null);

  protected readonly confirmingClear = signal(false);
  protected readonly clearing = signal(false);
  protected readonly clearError = signal('');

  protected readonly openAi = computed(() => this.provider() === 'OpenAiCompatible');
  /** The base URL that would be sent: none for a provider that has no base URL. */
  private readonly sentBaseUrl = computed(() =>
    this.openAi() ? this.baseUrl().trim() || null : null,
  );

  protected readonly keyRequired = computed(() => {
    const s = this.saved();
    if (!s?.configured) return true;
    return s.provider !== this.provider() || !sameUrl(this.sentBaseUrl(), s.baseUrl);
  });
  /** True when a key is saved but this change would send it somewhere new. */
  protected readonly keyMoves = computed(() => !!this.saved()?.configured && this.keyRequired());
  protected readonly keyPlaceholder = computed(() => {
    const s = this.saved();
    return s?.configured && s.keyLast4 ? `••••${s.keyLast4}` : '';
  });

  protected readonly baseUrlError = computed(() => {
    const url = this.sentBaseUrl();
    if (url === null) return '';
    try {
      return new URL(url).protocol === 'https:' ? '' : 'The base URL must use HTTPS.';
    } catch {
      return 'The base URL must be a full URL, such as https://models.example.com/v1.';
    }
  });
  protected readonly keyError = computed(() => {
    const k = this.key().trim();
    return k && k.length < MIN_KEY_LENGTH ? `A key has at least ${MIN_KEY_LENGTH} characters.` : '';
  });
  private readonly capValue = computed<number | null | undefined>(() => {
    const raw = this.cap().trim();
    if (!raw) return null;
    const n = Number(raw);
    return Number.isSafeInteger(n) && n > 0 ? n : undefined;
  });
  protected readonly capError = computed(() =>
    this.capValue() === undefined ? 'The cap is a whole number of tokens, above zero.' : '',
  );

  protected readonly canSave = computed(
    () =>
      !this.saving() &&
      !!this.modelName().trim() &&
      !this.baseUrlError() &&
      !this.keyError() &&
      !this.capError() &&
      (!this.keyRequired() || !!this.key().trim()),
  );

  protected readonly total = computed(() => {
    const u = this.usage();
    return u ? u.inputTokens + u.outputTokens : 0;
  });
  protected readonly percent = computed(() => {
    const cap = this.usage()?.monthlyTokenCap;
    // Floored, so 100% only shows once the cap is really reached.
    return cap ? Math.min(100, Math.floor((this.total() / cap) * 100)) : null;
  });

  /** Bumped on every load, so an answer for a project no longer on screen is dropped. */
  private run = 0;

  constructor() {
    effect(() => {
      const id = this.project();
      untracked(() => void this.open(id));
    });
  }

  protected async open(id: string): Promise<void> {
    const run = ++this.run;
    this.load.set('loading');
    this.saved.set(null);
    this.usage.set(null);
    this.testResult.set(null);
    this.saveError.set('');
    this.savedNote.set('');
    this.confirmingClear.set(false);
    try {
      const project = (await this.api.projects()).find((p) => p.id === id) ?? null;
      if (run !== this.run) return;
      this.summary.set(project);
      if (!project) return this.load.set('no-project');
      if (!MANAGERS.includes(project.role)) return this.load.set('not-allowed');

      const [settings, usage] = await Promise.all([
        this.api.modelSettings(id),
        this.api.modelUsage(id).catch(() => null),
      ]);
      if (run !== this.run) return;
      this.show(settings);
      // With settings off there is no chat, so no usage to show.
      this.usage.set(settings.enabled ? usage : null);
      this.load.set('ready');
    } catch (e) {
      if (run !== this.run) return;
      this.loadError.set(message(e, 'Could not load the model settings.'));
      this.load.set('failed');
    }
  }

  protected typed(field: Field, event: Event): void {
    this[field].set((event.target as HTMLInputElement).value);
    this.edited();
  }

  protected chooseProvider(event: Event): void {
    this.provider.set((event.target as HTMLSelectElement).value as ModelProvider);
    this.edited();
  }

  /** A test result or "Saved" was about the settings before this edit. */
  private edited(): void {
    this.savedNote.set('');
    this.testResult.set(null);
  }

  protected async save(event: Event): Promise<void> {
    event.preventDefault();
    if (!this.canSave()) return;
    const id = this.project();
    this.saving.set(true);
    this.saveError.set('');
    this.savedNote.set('');
    try {
      const view = await this.api.saveModelSettings(id, {
        provider: this.provider(),
        model: this.modelName().trim(),
        baseUrl: this.sentBaseUrl(),
        apiKey: this.key().trim() || null,
        monthlyTokenCap: this.capValue() ?? null,
      });
      this.show(view);
      // The old result was for the old settings.
      this.testResult.set(null);
      this.savedNote.set('Saved');
      void this.refreshUsage(id);
      // Save was disabled while saving, which drops focus; give it back.
      this.focusAfterRender('#model-save');
    } catch (e) {
      this.saveError.set(message(e, 'Could not save the model settings.'));
      this.focusAfterRender('#model-save-error');
    } finally {
      this.saving.set(false);
    }
  }

  protected async test(): Promise<void> {
    this.testing.set(true);
    this.testResult.set(null);
    try {
      this.testResult.set(await this.api.testModel(this.project()));
    } catch (e) {
      this.testResult.set({
        ok: false,
        code: null,
        message: message(e, 'The test could not run.'),
      });
    } finally {
      this.testing.set(false);
    }
  }

  protected askClear(): void {
    this.clearError.set('');
    this.confirmingClear.set(true);
    this.focusAfterRender('[data-confirm]');
  }

  protected cancelClear(): void {
    this.confirmingClear.set(false);
    this.focusAfterRender('[data-clear]');
  }

  protected async clear(): Promise<void> {
    const id = this.project();
    this.clearing.set(true);
    this.clearError.set('');
    try {
      await this.api.clearModelSettings(id);
      this.show({
        ...(this.saved() as ModelSettingsView),
        configured: false,
        provider: null,
        model: null,
        baseUrl: null,
        keyLast4: null,
        monthlyTokenCap: null,
        updatedBy: null,
        updatedAt: null,
      });
      this.testResult.set(null);
      this.savedNote.set('Cleared');
      void this.refreshUsage(id);
      this.focusAfterRender('#model-title');
    } catch (e) {
      this.clearError.set(message(e, 'Could not clear the model settings.'));
      this.focusAfterRender('[data-clear]');
    } finally {
      this.confirmingClear.set(false);
      this.clearing.set(false);
    }
  }

  /** Puts the stored settings in the form. The key field is emptied, never filled. */
  private show(view: ModelSettingsView): void {
    this.saved.set(view);
    this.provider.set(view.provider ?? 'Anthropic');
    this.modelName.set(view.model ?? '');
    this.baseUrl.set(view.baseUrl ?? '');
    this.key.set('');
    this.cap.set(view.monthlyTokenCap == null ? '' : String(view.monthlyTokenCap));
  }

  private async refreshUsage(id: string): Promise<void> {
    try {
      const usage = await this.api.modelUsage(id);
      if (id === this.project()) this.usage.set(usage);
    } catch {
      // The settings are saved; the usage line keeps its last numbers.
    }
  }

  private focusAfterRender(selector: string): void {
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>(selector)?.focus(), {
      injector: this.injector,
    });
  }
}
