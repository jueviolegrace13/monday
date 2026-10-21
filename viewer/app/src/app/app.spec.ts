import { TestBed } from '@angular/core/testing';
import { App } from './app';
import { MondayApi, ProjectSummary } from './api/monday-api';

const project = (id: string, name: string): ProjectSummary => ({
  id,
  name,
  role: 'Owner',
  environments: [],
});

async function render(projects: () => Promise<ProjectSummary[]>) {
  await TestBed.configureTestingModule({
    imports: [App],
    providers: [{ provide: MondayApi, useValue: { projects } }],
  }).compileComponents();
  const fixture = TestBed.createComponent(App);
  await fixture.whenStable();
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('App', () => {
  it('lists the projects in the picker and shows the first', async () => {
    const page = await render(async () => [project('a', 'Monday'), project('b', 'Garage')]);

    expect([...page.querySelectorAll('option')].map((o) => o.textContent?.trim())).toEqual([
      'Monday',
      'Garage',
    ]);
    expect(page.querySelector('h1')?.textContent).toBe('Monday');
  });

  it('says so when there are no projects', async () => {
    const page = await render(async () => []);

    expect(page.querySelector('select')).toBeNull();
    expect(page.querySelector('h1')?.textContent).toBe('No projects yet');
  });

  it('shows the failure and offers a retry', async () => {
    const page = await render(async () => {
      throw new Error('Unauthorized');
    });

    expect(page.querySelector('h1')?.textContent).toBe('Could not load projects');
    expect(page.textContent).toContain('Unauthorized');
    expect(page.querySelector('button')?.textContent).toBe('Try again');
  });
});
