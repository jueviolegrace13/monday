import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import {
  ApiError,
  IssuedToken,
  MondayApi,
  PersonalTokenInput,
  PersonalTokenView,
  ProjectSummary,
} from '../api/monday-api';
import { TokensComponent } from './tokens.component';

const PLAIN = 'mpat_abc123_s3cr3tS3cr3tS3cr3t';
const MONDAY = '11111111-1111-1111-1111-111111111111';
const GARAGE = '22222222-2222-2222-2222-222222222222';

const view = (over: Partial<PersonalTokenView> = {}): PersonalTokenView => ({
  id: 'aaaaaaaa-0000-0000-0000-000000000001',
  name: 'laptop',
  prefix: 'mpat_abc123',
  scope: 'read',
  projects: null,
  createdAt: '2026-10-01T00:00:00Z',
  expiresAt: '2026-12-30T12:00:00Z',
  revokedAt: null,
  lastUsedAt: null,
  active: true,
  ...over,
});

const projects: ProjectSummary[] = [
  { id: MONDAY, name: 'Monday', role: 'Owner', environments: [] },
  { id: GARAGE, name: 'Garage', role: 'Monitor', environments: [] },
];

/** An in-memory /me/tokens: the plain token exists only in what createToken returns. */
function fakeApi(initial: PersonalTokenView[] = []) {
  let tokens = [...initial];
  const calls = { list: 0, created: [] as PersonalTokenInput[], revoked: [] as string[] };
  const api = {
    projects: async () => projects,
    tokens: async () => {
      calls.list++;
      return tokens.map((t) => ({ ...t }));
    },
    createToken: async (input: PersonalTokenInput): Promise<IssuedToken> => {
      calls.created.push(input);
      if (!input.name.trim())
        throw new ApiError(400, 'A token name is required', 'token.name_required');
      const token = view({
        id: 'aaaaaaaa-0000-0000-0000-000000000002',
        name: input.name,
        prefix: 'mpat_new999',
        scope: input.scope,
        projects: input.projects,
      });
      tokens = [token, ...tokens];
      return { token, plain: PLAIN };
    },
    revokeToken: async (id: string) => {
      calls.revoked.push(id);
      tokens = tokens.map((t) =>
        t.id === id ? { ...t, revokedAt: '2026-10-21T00:00:00Z', active: false } : t,
      );
    },
  };
  return { api, calls };
}

async function render(api: object) {
  await TestBed.configureTestingModule({
    imports: [TokensComponent],
    providers: [provideRouter([]), { provide: MondayApi, useValue: api }],
  }).compileComponents();
  const fixture = TestBed.createComponent(TokensComponent);
  await settle(fixture);
  return { page: fixture.nativeElement as HTMLElement, fixture };
}

/** Lets the fake API's promises (and the handlers chained on them) finish, then renders. */
async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
  fixture.detectChanges();
  await new Promise((resolve) => setTimeout(resolve));
  await fixture.whenStable();
  fixture.detectChanges();
}

const button = (page: HTMLElement, text: string) =>
  [...page.querySelectorAll('button')].find((b) => b.textContent?.trim() === text) as
    HTMLButtonElement | undefined;

function type(page: HTMLElement, label: string, value: string) {
  const id = [...page.querySelectorAll('label')]
    .find((l) => l.textContent?.trim().startsWith(label))
    ?.getAttribute('for');
  const input = page.querySelector<HTMLInputElement>(`#${id}`)!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

describe('TokensComponent', () => {
  it('newTokenShownOnceThenMasked', async () => {
    const { api, calls } = fakeApi();
    const { page, fixture } = await render(api);

    type(page, 'Name', 'claude code');
    fixture.detectChanges();
    button(page, 'Create token')!.click();
    await settle(fixture);

    // Shown once, in full, with a copy button and the command to connect Claude Code.
    expect(calls.created).toEqual([
      { name: 'claude code', scope: 'read', projects: null, lifetimeDays: 90 },
    ]);
    const shown = page.querySelector('.issued')!;
    expect(shown.textContent).toContain(PLAIN);
    expect(shown.textContent).toContain(
      `claude mcp add --transport http monday ${location.origin}/mcp --header "Authorization: Bearer ${PLAIN}"`,
    );
    expect(button(page, 'Copy token')).toBeDefined();
    // The list has the new token by its prefix only.
    expect(page.querySelector('tbody')!.textContent).toContain('mpat_new999');
    expect(page.querySelector('tbody')!.textContent).not.toContain(PLAIN);

    button(page, 'Done')!.click();
    await settle(fixture);
    expect(page.textContent).not.toContain(PLAIN);
    expect(page.querySelector('.issued')).toBeNull();

    // A fresh visit (navigating back) lists the token again; the plain text is gone for good.
    fixture.destroy();
    const again = TestBed.createComponent(TokensComponent);
    await settle(again);
    const second = again.nativeElement as HTMLElement;
    expect(second.querySelector('tbody')!.textContent).toContain('mpat_new999');
    expect(second.textContent).not.toContain(PLAIN);
  });

  it('lists each token with its scope, projects, expiry, last use and state', async () => {
    const { api } = fakeApi([
      view({ name: 'laptop', lastUsedAt: '2026-10-20T12:00:00Z' }),
      view({
        id: 'aaaaaaaa-0000-0000-0000-000000000003',
        name: 'ci',
        prefix: 'mpat_ci0000',
        scope: 'read_propose',
        projects: [GARAGE],
        revokedAt: '2026-10-10T00:00:00Z',
        active: false,
      }),
      view({
        id: 'aaaaaaaa-0000-0000-0000-000000000004',
        name: 'old',
        prefix: 'mpat_old000',
        expiresAt: '2026-01-01T00:00:00Z',
        active: false,
      }),
    ]);
    const { page } = await render(api);

    const rows = [...page.querySelectorAll('tbody tr')].map((r) =>
      [...r.querySelectorAll('td')].map((c) => c.textContent?.replace(/\s+/g, ' ').trim()),
    );
    expect(rows.length).toBe(3);
    expect(rows[0].slice(0, 6)).toEqual([
      'laptop mpat_abc123',
      'read',
      'All projects',
      '2026-12-30',
      '2026-10-20',
      'Active',
    ]);
    expect(rows[1].slice(0, 6)).toEqual([
      'ci mpat_ci0000',
      'read + propose',
      'Garage',
      '2026-12-30',
      'never',
      'Revoked',
    ]);
    expect(rows[2][5]).toBe('Expired');
    // Only an active token can be revoked.
    expect(page.querySelectorAll('tbody button').length).toBe(1);
  });

  it('revokes only after confirming', async () => {
    const { api, calls } = fakeApi([view()]);
    const { page, fixture } = await render(api);

    button(page, 'Revoke')!.click();
    fixture.detectChanges();
    expect(calls.revoked).toEqual([]);
    expect(page.textContent).toContain('Revoke laptop?');

    button(page, 'Cancel')!.click();
    fixture.detectChanges();
    expect(page.textContent).not.toContain('Revoke laptop?');

    button(page, 'Revoke')!.click();
    fixture.detectChanges();
    button(page, 'Yes, revoke')!.click();
    await settle(fixture);

    expect(calls.revoked).toEqual(['aaaaaaaa-0000-0000-0000-000000000001']);
    expect(page.querySelector('tbody tr td:nth-child(6)')!.textContent?.trim()).toBe('Revoked');
    expect(page.querySelectorAll('tbody button').length).toBe(0);
  });

  it('limits a token to the projects picked and keeps the expiry in range', async () => {
    const { api, calls } = fakeApi();
    const { page, fixture } = await render(api);

    type(page, 'Name', 'agent');
    (page.querySelector('input[value="read_propose"]') as HTMLInputElement).click();
    (page.querySelector('input[value="some"]') as HTMLInputElement).click();
    fixture.detectChanges();
    // Limited, but nothing picked yet: nothing to create.
    expect(button(page, 'Create token')!.disabled).toBe(true);

    (page.querySelector(`input[name="project"][value="${GARAGE}"]`) as HTMLInputElement).click();
    type(page, 'Expires in', '400');
    fixture.detectChanges();
    expect(button(page, 'Create token')!.disabled).toBe(true);
    expect(page.textContent).toContain('1 to 365 days');

    type(page, 'Expires in', '30');
    fixture.detectChanges();
    button(page, 'Create token')!.click();
    await settle(fixture);

    expect(calls.created).toEqual([
      { name: 'agent', scope: 'read_propose', projects: [GARAGE], lifetimeDays: 30 },
    ]);
  });

  it('shows the API refusal plainly', async () => {
    const { api } = fakeApi();
    api.createToken = async () => {
      throw new ApiError(400, 'A token can last one year at most.', 'token.lifetime_max');
    };
    const { page, fixture } = await render(api);

    type(page, 'Name', 'x');
    fixture.detectChanges();
    button(page, 'Create token')!.click();
    await settle(fixture);

    expect(page.querySelector('[role="alert"]')!.textContent).toContain(
      'A token can last one year at most.',
    );
    expect(page.querySelector('.issued')).toBeNull();
  });

  it('says so when the list cannot load', async () => {
    const { api } = fakeApi();
    api.tokens = async () => {
      throw new ApiError(401, 'Unauthorized');
    };
    const { page } = await render(api);

    expect(page.querySelector('[role="alert"]')!.textContent).toContain('Unauthorized');
  });

  it('moves focus to the one-time card after a create', async () => {
    const { api } = fakeApi();
    const { page, fixture } = await render(api);

    type(page, 'Name', 'laptop');
    fixture.detectChanges();
    button(page, 'Create token')!.click();
    await settle(fixture);

    expect(document.activeElement).toBe(page.querySelector('#issued-title'));
    expect(document.activeElement?.getAttribute('tabindex')).toBe('-1');
  });

  it('forgets the previous plain token on the next create', async () => {
    const { api } = fakeApi();
    let n = 0;
    const create = api.createToken;
    api.createToken = async (input) => ({ ...(await create(input)), plain: `${PLAIN}${++n}` });
    const { page, fixture } = await render(api);

    type(page, 'Name', 'one');
    fixture.detectChanges();
    button(page, 'Create token')!.click();
    await settle(fixture);
    expect(page.textContent).toContain(`${PLAIN}1`);

    type(page, 'Name', 'two');
    fixture.detectChanges();
    button(page, 'Create token')!.click();
    await settle(fixture);

    expect(page.textContent).toContain(`${PLAIN}2`);
    expect(page.textContent).not.toContain(`${PLAIN}1`);
  });

  it('focuses the confirmation, and returns focus after cancel and after revoke', async () => {
    const { api } = fakeApi([view()]);
    const { page, fixture } = await render(api);

    button(page, 'Revoke')!.click();
    await settle(fixture);
    expect(document.activeElement).toBe(button(page, 'Yes, revoke'));

    button(page, 'Cancel')!.click();
    await settle(fixture);
    expect(document.activeElement).toBe(button(page, 'Revoke'));

    button(page, 'Revoke')!.click();
    await settle(fixture);
    button(page, 'Yes, revoke')!.click();
    await settle(fixture);
    // The row has no Revoke button any more: focus lands on the list's heading.
    expect(document.activeElement).toBe(page.querySelector('#list-title'));
  });

  it('shows a failed revoke and reloads the row from the server', async () => {
    const { api } = fakeApi([view()]);
    const { page, fixture } = await render(api);
    // Someone revoked it elsewhere: the server says so, and the list now has it revoked.
    api.revokeToken = async (id: string) => {
      api.tokens = async () => [view({ id, revokedAt: '2026-10-21T00:00:00Z', active: false })];
      throw new ApiError(400, 'This token is already revoked.', 'token.already_revoked');
    };

    button(page, 'Revoke')!.click();
    await settle(fixture);
    button(page, 'Yes, revoke')!.click();
    await settle(fixture);

    expect(page.querySelector('[role="alert"]')!.textContent).toContain(
      'This token is already revoked.',
    );
    expect(page.querySelector('tbody tr td:nth-child(6)')!.textContent?.trim()).toBe('Revoked');
    expect(document.activeElement).toBe(page.querySelector('#list-title'));
  });
});
