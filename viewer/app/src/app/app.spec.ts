import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { App } from './app';
import { routes } from './app.routes';
import { GraphView, MondayApi, ProjectSummary } from './api/monday-api';
import { VIS_LOADER } from './graph/vis';

const project = (id: string, name: string, role = 'Owner'): ProjectSummary => ({
  id,
  name,
  role,
  environments: [],
});

async function render(projects: () => Promise<ProjectSummary[]>) {
  await TestBed.configureTestingModule({
    imports: [App],
    providers: [
      provideRouter(routes, withComponentInputBinding()),
      {
        provide: MondayApi,
        useValue: {
          projects,
          graph: async (): Promise<GraphView | null> => null,
          tokens: async () => [],
          modelSettings: async () => ({ enabled: true, configured: false }),
          modelUsage: async () => ({
            month: '2026-10',
            inputTokens: 0,
            outputTokens: 0,
            monthlyTokenCap: null,
            capReached: false,
          }),
        },
      },
      // jsdom has no canvas: the graph library never loads in these tests.
      { provide: VIS_LOADER, useValue: () => new Promise(() => undefined) },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(App);
  await fixture.whenStable();
  fixture.detectChanges();
  return { page: fixture.nativeElement as HTMLElement, fixture };
}

const key = (k: string, target: EventTarget = document.body) =>
  target.dispatchEvent(new KeyboardEvent('keydown', { key: k, bubbles: true, cancelable: true }));

describe('App', () => {
  it('lists the projects in the picker and opens the first', async () => {
    const { page } = await render(async () => [project('a', 'Monday'), project('b', 'Garage')]);

    expect([...page.querySelectorAll('option')].map((o) => o.textContent?.trim())).toEqual([
      'Monday',
      'Garage',
    ]);
    expect(page.querySelector('h1')?.textContent).toBe('Monday');
    expect(page.querySelector('app-graph-canvas')).not.toBeNull();
  });

  it('says so when there are no projects', async () => {
    const { page } = await render(async () => []);

    expect(page.querySelector('select')).toBeNull();
    expect(page.querySelector('h1')?.textContent).toBe('No projects yet');
  });

  it('shows the failure and offers a retry', async () => {
    const { page } = await render(async () => {
      throw new Error('Unauthorized');
    });

    expect(page.querySelector('h1')?.textContent).toBe('Could not load projects');
    expect(page.textContent).toContain('Unauthorized');
    expect(page.querySelector('button')?.textContent).toBe('Try again');
  });

  it('opens the chat with / but not while typing, and the input has a label', async () => {
    const { page, fixture } = await render(async () => [project('a', 'Monday')]);
    expect(page.querySelector('textarea')).toBeNull();

    key('/', page.querySelector('select')!);
    fixture.detectChanges();
    expect(page.querySelector('textarea')).toBeNull();

    key('/');
    fixture.detectChanges();
    const input = page.querySelector('textarea')!;
    expect(input).not.toBeNull();
    expect(page.querySelector(`label[for="${input.id}"]`)).not.toBeNull();
    expect(page.querySelector('.stream')?.getAttribute('aria-live')).toBe('polite');
  });

  it('marks a Monitor as ask-only', async () => {
    const { page, fixture } = await render(async () => [project('a', 'Monday', 'Monitor')]);
    key('/');
    fixture.detectChanges();

    expect(page.querySelector('.mode')?.textContent).toBe('ask only');
  });

  it('links to the tokens screen, which replaces the graph until you go back', async () => {
    const { page, fixture } = await render(async () => [project('a', 'Monday')]);
    const link = [...page.querySelectorAll<HTMLAnchorElement>('a.nav')].find(
      (a) => a.textContent?.trim() === 'Tokens',
    )!;
    expect(link.getAttribute('href')).toBe('/settings/tokens');

    await TestBed.inject(Router).navigateByUrl('/settings/tokens');
    await fixture.whenStable();
    fixture.detectChanges();
    expect(page.querySelector('app-tokens')).not.toBeNull();
    expect(page.querySelector('app-graph-canvas')).toBeNull();

    await TestBed.inject(Router).navigateByUrl('/');
    await fixture.whenStable();
    fixture.detectChanges();
    expect(page.querySelector('app-tokens')).toBeNull();
    expect(page.querySelector('app-graph-canvas')).not.toBeNull();
  });

  it('keeps / and Esc for the graph while the tokens screen is open', async () => {
    const { page, fixture } = await render(async () => [project('a', 'Monday')]);
    await TestBed.inject(Router).navigateByUrl('/settings/tokens');
    await fixture.whenStable();
    fixture.detectChanges();

    expect(key('/')).toBe(true); // not taken: the default still happens
    expect(key('Escape')).toBe(true);

    await TestBed.inject(Router).navigateByUrl('/');
    await fixture.whenStable();
    fixture.detectChanges();
    expect(page.querySelector('textarea')).toBeNull();
  });

  it('links Owners and Admins to model settings for the open project', async () => {
    const { page, fixture } = await render(async () => [project('a', 'Monday')]);
    const link = [...page.querySelectorAll<HTMLAnchorElement>('a.nav')].find(
      (a) => a.textContent?.trim() === 'Model',
    )!;
    expect(link.getAttribute('href')).toBe('/settings/model?project=a');

    await TestBed.inject(Router).navigateByUrl('/settings/model?project=a');
    await fixture.whenStable();
    fixture.detectChanges();
    expect(page.querySelector('app-model')).not.toBeNull();
    expect(page.querySelector('app-graph-canvas')).toBeNull();
  });

  it('shows no model link to Debuggers and Monitors', async () => {
    const { page } = await render(async () => [project('a', 'Monday', 'Debugger')]);
    const labels = [...page.querySelectorAll('a.nav')].map((a) => a.textContent?.trim());
    expect(labels).toEqual(['Tokens']);
  });

  it('moves the model screen to the project picked in the bar', async () => {
    const { page, fixture } = await render(async () => [
      project('a', 'Monday'),
      project('b', 'Garage'),
    ]);
    const router = TestBed.inject(Router);
    await router.navigateByUrl('/settings/model?project=a');
    await fixture.whenStable();
    fixture.detectChanges();

    const select = page.querySelector('select')!;
    select.value = 'b';
    select.dispatchEvent(new Event('change'));
    await fixture.whenStable();

    expect(router.url).toBe('/settings/model?project=b');
  });
});
