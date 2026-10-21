import { expect, test } from '@playwright/test';
import { PROJECT, stubApi } from './stubs';

// The model settings screen against a stubbed /projects/{id}/model: the key is never sent back,
// and changing the provider asks for it again.
const KEY = 'sk-ant-api-key-ABCD';

test('modelSettingsKeepTheKeyWriteOnly', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await stubApi(page);

  let settings = {
    enabled: true,
    configured: true,
    provider: 'OpenAiCompatible',
    model: 'gpt-4.1',
    baseUrl: 'https://models.example.com/v1',
    keyLast4: 'x9Qz',
    monthlyTokenCap: 1000000,
    updatedBy: 'dev',
    updatedAt: '2026-10-20T08:00:00Z',
  };
  const puts: Record<string, unknown>[] = [];
  // Registered after stubApi, so it answers first for the model routes.
  await page.route(`**/projects/${PROJECT}/model/**`, async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path.endsWith('/usage'))
      return route.fulfill({
        json: {
          month: '2026-10',
          inputTokens: 612400,
          outputTokens: 98100,
          monthlyTokenCap: settings.monthlyTokenCap,
          capReached: false,
        },
      });
    if (path.endsWith('/test'))
      return route.fulfill({
        json: { ok: false, code: 'model.unauthorized', message: 'The provider refused the key.' },
      });
    if (request.method() === 'PUT') {
      const body = request.postDataJSON() as Record<string, unknown>;
      puts.push(body);
      settings = {
        ...settings,
        provider: body['provider'] as string,
        model: body['model'] as string,
        baseUrl: body['baseUrl'] as string,
        keyLast4: body['apiKey'] ? (body['apiKey'] as string).slice(-4) : settings.keyLast4,
      };
      return route.fulfill({ json: settings });
    }
    return route.fulfill({ json: settings });
  });

  await page.goto('/');
  await page.getByRole('link', { name: 'Model' }).click();
  await expect(page).toHaveURL(new RegExp(`/settings/model\\?project=${PROJECT}$`));
  await expect(page.getByRole('heading', { name: 'Model', exact: true })).toBeVisible();

  const key = page.getByLabel('API key');
  await expect(key).toHaveValue('');
  await expect(key).toHaveAttribute('placeholder', '••••x9Qz');
  await expect(page.getByLabel('Base URL')).toHaveValue('https://models.example.com/v1');
  await expect(page.locator('.usage')).toContainText('710,500 of 1,000,000');
  await page.screenshot({ path: 'test-results/model-settings.png', fullPage: true });

  await page.getByRole('button', { name: 'Test connection' }).click();
  await expect(page.getByRole('alert')).toContainText('The provider refused the key.');

  // Another provider: no base URL, and the key must be typed again.
  await page.getByLabel('Provider').selectOption('Anthropic');
  await expect(page.getByLabel('Base URL')).toHaveCount(0);
  await expect(page.getByText('Enter the key again')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save' })).toBeDisabled();
  await page.getByLabel('Model', { exact: true }).fill('claude-sonnet-4-5');
  await key.fill(KEY);
  await page.screenshot({ path: 'test-results/model-key-again.png', fullPage: true });
  await page.getByRole('button', { name: 'Save' }).click();

  await expect(key).toHaveValue('');
  await expect(key).toHaveAttribute('placeholder', '••••ABCD');
  expect(puts).toEqual([
    {
      provider: 'Anthropic',
      model: 'claude-sonnet-4-5',
      baseUrl: null,
      apiKey: KEY,
      monthlyTokenCap: 1000000,
    },
  ]);
  await expect(page.locator('body')).not.toContainText(KEY);

  // Back to the graph without a reload.
  await page.getByRole('link', { name: 'Back to the graph' }).click();
  await expect(page.locator('.graph canvas')).toBeVisible();
  expect(errors).toEqual([]);
});
