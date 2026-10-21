import { expect, test } from '@playwright/test';
import { PROJECT, stubApi } from './stubs';

// The tokens screen against a stubbed /me/tokens: the plain token is shown once and then gone.
const PLAIN = 'mpat_new999_s3cr3tS3cr3tS3cr3t';

test('newTokenShownOnceThenGone', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await stubApi(page);

  const tokens: object[] = [];
  const posted: unknown[] = [];
  await page.route('**/me/tokens/**', async (route) => {
    const request = route.request();
    if (request.method() === 'POST') {
      posted.push(request.postDataJSON());
      const token = {
        id: 'aaaaaaaa-0000-0000-0000-000000000002',
        name: 'claude code',
        prefix: 'mpat_new999',
        scope: 'read_propose',
        projects: [PROJECT],
        createdAt: '2026-10-21T12:00:00Z',
        expiresAt: '2027-01-19T12:00:00Z',
        active: true,
      };
      tokens.unshift(token);
      return route.fulfill({ status: 201, json: { token, plain: PLAIN } });
    }
    return route.fulfill({ json: tokens });
  });

  await page.goto('/');
  await page.getByRole('link', { name: 'Tokens' }).click();
  await expect(page).toHaveURL(/\/settings\/tokens$/);
  await expect(page.getByRole('heading', { name: 'Personal tokens' })).toBeVisible();
  await expect(page.getByText('No tokens yet')).toBeVisible();

  await page.getByLabel('Name').fill('claude code');
  await page.getByLabel('Read and propose decisions').check();
  await page.getByLabel('Only these').check();
  await page.getByLabel('Monday', { exact: true }).check();
  await page.getByRole('button', { name: 'Create token' }).click();

  const issued = page.locator('.issued');
  await expect(issued).toContainText(PLAIN);
  await expect(issued).toContainText(
    `claude mcp add --transport http monday http://localhost:4300/mcp --header "Authorization: Bearer ${PLAIN}"`,
  );
  expect(posted).toEqual([
    { name: 'claude code', scope: 'read_propose', projects: [PROJECT], lifetimeDays: 90 },
  ]);
  await expect(page.locator('tbody')).toContainText('mpat_new999');
  await page.screenshot({ path: 'test-results/tokens-issued.png', fullPage: true });

  // Away and back: the list is fetched again and has no plain token.
  await page.getByRole('link', { name: 'Back to the graph' }).click();
  await expect(page.locator('.graph canvas')).toBeVisible();
  await page.getByRole('link', { name: 'Tokens' }).click();
  await expect(page.locator('tbody')).toContainText('mpat_new999');
  await expect(page.locator('body')).not.toContainText(PLAIN);
  await page.screenshot({ path: 'test-results/tokens-list.png', fullPage: true });

  expect(errors).toEqual([]);
});
