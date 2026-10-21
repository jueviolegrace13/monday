import { TestBed } from '@angular/core/testing';
import { App } from './app';
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
      {
        provide: MondayApi,
        useValue: { projects, graph: async (): Promise<GraphView | null> => null },
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
});
