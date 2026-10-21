import { TestBed } from '@angular/core/testing';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import {
  ApiError,
  ModelSettingsInput,
  ModelSettingsView,
  ModelTestResult,
  ModelUsageView,
  MondayApi,
  ProjectSummary,
} from '../api/monday-api';
import { ModelComponent } from './model.component';

const MONDAY = '11111111-1111-1111-1111-111111111111';
const GARAGE = '22222222-2222-2222-2222-222222222222';

const projects: ProjectSummary[] = [
  { id: MONDAY, name: 'Monday', role: 'Owner', environments: [] },
  { id: GARAGE, name: 'Garage', role: 'Debugger', environments: [] },
];

const saved = (over: Partial<ModelSettingsView> = {}): ModelSettingsView => ({
  enabled: true,
  configured: true,
  provider: 'Anthropic',
  model: 'claude-sonnet-4-5',
  baseUrl: null,
  keyLast4: 'x9Qz',
  monthlyTokenCap: 1_000_000,
  updatedBy: 'rendee',
  updatedAt: '2026-10-21T00:00:00Z',
  ...over,
});

const none: ModelSettingsView = {
  enabled: true,
  configured: false,
  provider: null,
  model: null,
  baseUrl: null,
  keyLast4: null,
  monthlyTokenCap: null,
  updatedBy: null,
  updatedAt: null,
};

const usage = (over: Partial<ModelUsageView> = {}): ModelUsageView => ({
  month: '2026-10',
  inputTokens: 200_000,
  outputTokens: 50_000,
  monthlyTokenCap: 1_000_000,
  capReached: false,
  ...over,
});

/** What the server sends: it leaves null fields out of its JSON (WhenWritingNull). */
const wire = <T>(value: T): T =>
  JSON.parse(JSON.stringify(value, (_k, v: unknown) => (v === null ? undefined : v))) as T;

/** An in-memory /projects/{id}/model that mirrors the server's key rule and its JSON. */
function fakeApi(initial: ModelSettingsView = saved(), used: ModelUsageView = usage()) {
  let settings = { ...initial };
  const calls = {
    saved: [] as ModelSettingsInput[],
    cleared: 0,
    tested: 0,
    settingsFor: [] as string[],
  };
  let test: ModelTestResult = { ok: true, code: null, message: null };
  const api = {
    projects: async () => projects,
    modelSettings: async (id: string) => {
      calls.settingsFor.push(id);
      if (projects.find((p) => p.id === id)?.role === 'Debugger')
        throw new ApiError(403, 'Not allowed', 'access.denied');
      return wire(settings);
    },
    modelUsage: async () => wire({ ...used, monthlyTokenCap: settings.monthlyTokenCap }),
    saveModelSettings: async (_id: string, input: ModelSettingsInput) => {
      calls.saved.push(input);
      if (!input.apiKey && settings.configured && input.provider !== settings.provider)
        throw new ApiError(400, 'Enter the API key again to change the provider or base URL.');
      settings = saved({
        provider: input.provider,
        model: input.model,
        baseUrl: input.baseUrl,
        keyLast4: input.apiKey ? input.apiKey.slice(-4) : settings.keyLast4,
        monthlyTokenCap: input.monthlyTokenCap,
      });
      return wire(settings);
    },
    clearModelSettings: async () => {
      calls.cleared++;
      settings = { ...none };
    },
    testModel: async () => {
      calls.tested++;
      return wire(test);
    },
  };
  return { api, calls, setTest: (r: ModelTestResult) => (test = r) };
}

async function render(api: object, project = MONDAY) {
  TestBed.configureTestingModule({
    providers: [
      provideRouter(
        [{ path: 'settings/model', component: ModelComponent }],
        withComponentInputBinding(),
      ),
      { provide: MondayApi, useValue: api },
    ],
  });
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(`/settings/model?project=${project}`);
  const page = harness.routeNativeElement!;
  const settle = async () => {
    harness.detectChanges();
    await new Promise((resolve) => setTimeout(resolve));
    await harness.fixture.whenStable();
    harness.detectChanges();
  };
  await settle();
  return { page, settle, harness };
}

const button = (page: HTMLElement, text: string) =>
  [...page.querySelectorAll('button')].find((b) => b.textContent?.trim() === text) as
    HTMLButtonElement | undefined;

const field = <T extends HTMLElement = HTMLInputElement>(page: HTMLElement, label: string) => {
  const id = [...page.querySelectorAll('label')]
    .find((l) => l.textContent?.trim().startsWith(label))
    ?.getAttribute('for');
  return id ? page.querySelector<T>(`#${id}`) : null;
};

function type(page: HTMLElement, label: string, value: string) {
  const input = field(page, label)!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

function choose(page: HTMLElement, provider: string) {
  const select = field<HTMLSelectElement>(page, 'Provider')!;
  select.value = provider;
  select.dispatchEvent(new Event('change'));
}

describe('ModelComponent', () => {
  it('baseUrlOnlyForOpenAiCompatible', async () => {
    const { api, calls } = fakeApi(saved({ provider: 'Anthropic' }));
    const { page, settle } = await render(api);

    expect(field(page, 'Base URL')).toBeNull();

    choose(page, 'OpenAiCompatible');
    await settle();
    expect(field(page, 'Base URL')).not.toBeNull();

    // Typed for OpenAI-compatible, then switched away: the URL is not sent for another provider.
    type(page, 'Base URL', 'https://models.example.com/v1');
    choose(page, 'Gemini');
    await settle();
    expect(field(page, 'Base URL')).toBeNull();
    type(page, 'API key', 'gm-key-12345678');
    await settle();
    button(page, 'Save')!.click();
    await settle();

    expect(calls.saved[0]).toEqual({
      provider: 'Gemini',
      model: 'claude-sonnet-4-5',
      baseUrl: null,
      apiKey: 'gm-key-12345678',
      monthlyTokenCap: 1_000_000,
    });
  });

  it('keyNeverPrefilled', async () => {
    const { api, calls } = fakeApi();
    const { page, settle } = await render(api);

    const key = field(page, 'API key')!;
    expect(key.type).toBe('password');
    expect(key.value).toBe('');
    expect(key.placeholder).toBe('••••x9Qz');
    expect(page.textContent).toContain('Leave empty to keep the saved key');

    // Saving with the key left empty keeps it (null), and the field stays empty after a save.
    type(page, 'Model', 'claude-opus-4-1');
    await settle();
    button(page, 'Save')!.click();
    await settle();
    expect(calls.saved[0].apiKey).toBeNull();

    type(page, 'API key', 'sk-ant-new-key-ABCD');
    await settle();
    button(page, 'Save')!.click();
    await settle();
    expect(calls.saved[1].apiKey).toBe('sk-ant-new-key-ABCD');
    expect(field(page, 'API key')!.value).toBe('');
    expect(field(page, 'API key')!.placeholder).toBe('••••ABCD');
    expect(page.textContent).not.toContain('sk-ant-new-key-ABCD');
  });

  it('requires the key again when the provider or base URL changes, and says why', async () => {
    const { api, calls } = fakeApi(
      saved({ provider: 'OpenAiCompatible', baseUrl: 'https://models.example.com/v1' }),
    );
    const { page, settle } = await render(api);
    expect(button(page, 'Save')!.disabled).toBe(false);

    type(page, 'Base URL', 'https://other.example.com/v1');
    await settle();
    expect(button(page, 'Save')!.disabled).toBe(true);
    expect(page.textContent).toContain('Enter the key again');
    expect(field(page, 'API key')!.required).toBe(true);

    // The same URL, written the way the server stores it, needs no key.
    type(page, 'Base URL', 'https://models.example.com/v1');
    await settle();
    expect(button(page, 'Save')!.disabled).toBe(false);

    choose(page, 'Anthropic');
    await settle();
    expect(button(page, 'Save')!.disabled).toBe(true);
    expect(page.textContent).toContain('Enter the key again');

    type(page, 'API key', 'sk-ant-new-key-ABCD');
    await settle();
    expect(button(page, 'Save')!.disabled).toBe(false);
    button(page, 'Save')!.click();
    await settle();
    expect(calls.saved[0]).toMatchObject({ provider: 'Anthropic', apiKey: 'sk-ant-new-key-ABCD' });
  });

  it('requires a key when nothing is saved yet', async () => {
    const { api } = fakeApi(none, usage({ monthlyTokenCap: null }));
    const { page, settle } = await render(api);

    expect(field(page, 'API key')!.placeholder).toBe('');
    type(page, 'Model', 'claude-sonnet-4-5');
    await settle();
    expect(button(page, 'Save')!.disabled).toBe(true);
    type(page, 'API key', 'sk-ant-first-key-1234');
    await settle();
    expect(button(page, 'Save')!.disabled).toBe(false);
    expect(field(page, 'Monthly token cap')!.value).toBe('');
  });

  it('saves a first model when the server left the empty fields out', async () => {
    const { api, calls } = fakeApi(none, usage({ monthlyTokenCap: null }));
    const { page, settle } = await render(api);

    type(page, 'Model', 'claude-sonnet-4-5');
    type(page, 'API key', 'sk-ant-first-key-1234');
    await settle();
    button(page, 'Save')!.click();
    await settle();

    expect(calls.saved).toEqual([
      {
        provider: 'Anthropic',
        model: 'claude-sonnet-4-5',
        baseUrl: null,
        apiKey: 'sk-ant-first-key-1234',
        monthlyTokenCap: null,
      },
    ]);
    expect(page.querySelector('.usage')!.textContent).toContain('No monthly cap');
  });

  it('clears an old test result once the form changes', async () => {
    const { api } = fakeApi();
    const { page, settle } = await render(api);
    button(page, 'Test connection')!.click();
    await settle();
    expect(page.querySelector('.test')).not.toBeNull();

    type(page, 'Model', 'claude-opus-4-1');
    await settle();
    expect(page.querySelector('.test')).toBeNull();
  });

  it('testResultShown', async () => {
    const { api, calls, setTest } = fakeApi();
    const { page, settle } = await render(api);

    button(page, 'Test connection')!.click();
    await settle();
    expect(calls.tested).toBe(1);
    const ok = page.querySelector('[role="status"].test')!;
    expect(ok.textContent).toContain('Connection works');

    setTest({
      ok: false,
      code: 'model.unauthorized',
      message: 'The model provider refused the key.',
    });
    button(page, 'Test connection')!.click();
    await settle();
    const bad = page.querySelector('[role="alert"].test')!;
    expect(bad.textContent).toContain('The model provider refused the key.');
  });

  it('shows the save error from the server', async () => {
    const { api } = fakeApi();
    // The server refuses (here: a key moved without being typed); the screen shows its words.
    api.saveModelSettings = async () => {
      throw new ApiError(400, 'The base URL must use HTTPS.', 'model.base_url_https');
    };
    const { page, settle } = await render(api);
    button(page, 'Save')!.click();
    await settle();
    const alert = page.querySelector<HTMLElement>('[role="alert"]')!;
    expect(alert.textContent).toContain('The base URL must use HTTPS.');
    // Focus goes to the error, not to the page body (Save was disabled while saving).
    expect(document.activeElement).toBe(alert);
  });

  it('clears the settings only after a confirmation', async () => {
    const { api, calls } = fakeApi();
    const { page, settle } = await render(api);

    button(page, 'Clear settings')!.click();
    await settle();
    expect(calls.cleared).toBe(0);
    expect(page.textContent).toContain('Chat stops');
    button(page, 'Cancel')!.click();
    await settle();
    expect(calls.cleared).toBe(0);

    button(page, 'Clear settings')!.click();
    await settle();
    button(page, 'Yes, clear')!.click();
    await settle();
    expect(calls.cleared).toBe(1);
    expect(field(page, 'API key')!.placeholder).toBe('');
    expect(button(page, 'Clear settings')).toBeUndefined();
  });

  it('shows usage against the cap', async () => {
    const { api } = fakeApi(saved({ monthlyTokenCap: 1_000_000 }));
    const { page } = await render(api);
    const meter = page.querySelector('.usage')!;
    expect(meter.textContent).toContain('200,000');
    expect(meter.textContent).toContain('50,000');
    expect(meter.textContent).toContain('250,000 of 1,000,000');
    expect(meter.textContent).toContain('25%');
  });

  it('never shows 100% before the cap is reached', async () => {
    const { api } = fakeApi(
      saved({ monthlyTokenCap: 1_000_000 }),
      usage({ inputTokens: 999_000, outputTokens: 999, capReached: false }),
    );
    const { page } = await render(api);
    const meter = page.querySelector('.usage')!;
    expect(meter.textContent).toContain('(99%)');
    expect(meter.textContent).not.toContain('Cap reached');
  });

  it('says when the cap is reached, and when there is none', async () => {
    const reached = fakeApi(saved({ monthlyTokenCap: 200_000 }), usage({ capReached: true }));
    let { page } = await render(reached.api);
    expect(page.querySelector('.usage')!.textContent).toContain('Cap reached');

    TestBed.resetTestingModule();
    const open = fakeApi(saved({ monthlyTokenCap: null }));
    ({ page } = await render(open.api));
    expect(page.querySelector('.usage')!.textContent).toContain('No monthly cap');
  });

  it('shows Owners and Admins only, and tells others why', async () => {
    const { api, calls } = fakeApi();
    const { page } = await render(api, GARAGE);

    expect(page.textContent).toContain('Only Owners and Admins can change the model');
    expect(page.querySelector('form')).toBeNull();
    expect(calls.settingsFor).toEqual([]);
  });

  it('says plainly when model settings are disabled on the server', async () => {
    const { api } = fakeApi({ ...none, enabled: false });
    const { page } = await render(api);

    expect(page.textContent).toContain('MONDAY_SECRETS_KEY');
    expect(page.querySelector('form')).toBeNull();
    expect(page.querySelector('.usage')).toBeNull();
  });
});
