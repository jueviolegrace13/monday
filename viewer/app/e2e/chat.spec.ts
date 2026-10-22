import { expect, test } from '@playwright/test';
import { chatEvent, chatState, stubApi } from './stubs';

const CONVERSATION = '22222222-2222-2222-2222-222222222222';

let errors: string[] = [];

test.beforeEach(async ({ page }) => {
  errors = [];
  page.on('console', (m) => {
    if (m.type() === 'error') errors.push(m.text());
  });
  page.on('pageerror', (e) => errors.push(e.message));
  await stubApi(page);
  await page.goto('/');
  await expect(page.locator('.graph canvas')).toBeVisible();
});

test.afterEach(() => {
  expect(errors).toEqual([]);
});

test('slashOpensChat', async ({ page }) => {
  const input = page.getByLabel('Question');
  await expect(input).toHaveCount(0);

  await page.keyboard.press('/');
  await expect(input).toBeFocused();

  // Typing a slash in the input types it; it does not toggle anything.
  await page.keyboard.type('a/b');
  await expect(input).toHaveValue('a/b');

  // Esc in the input closes the chat; the button brings it back.
  await page.keyboard.press('Escape');
  await expect(input).toHaveCount(0);
  await page.getByRole('button', { name: 'Open chat (/)' }).click();
  await expect(input).toBeFocused();
});

test('scriptedAnswerPulsesAndStops', async ({ page }) => {
  const graph = page.locator('.graph');
  await page.keyboard.press('/');
  await page.getByLabel('Question').fill('Which endpoints are slow?');
  await page.keyboard.press('Enter');

  await expect.poll(async () => (await chatState(page)).bodies.length).toBe(1);
  expect(JSON.parse((await chatState(page)).bodies[0])).toEqual({
    message: 'Which endpoints are slow?',
  });

  await chatEvent(page, 'started', { conversationId: CONVERSATION });
  await chatEvent(page, 'tool', {
    name: 'traffic_stats',
    nodeIds: ['method:List', 'method:Get', 'method:Save'],
    isError: false,
  });
  await expect(page.getByRole('button', { name: /traffic_stats · 3 nodes/ })).toBeVisible();
  await expect(graph).toHaveAttribute('data-pulsing', 'method:List method:Get method:Save');
  await expect(page.getByText('3 nodes pulsing')).toBeAttached();

  await chatEvent(page, 'text', { text: 'Two endpoints are' });
  await expect(page.locator('.answer .text')).toHaveText('Two endpoints are');

  await page.getByRole('button', { name: 'Stop' }).click();

  expect((await chatState(page)).aborted).toBe(true);
  await expect(graph).toHaveAttribute('data-pulsing', '');
  await expect(page.locator('.answer .note')).toHaveText('stopped');
  await expect(page.getByRole('button', { name: 'Ask', exact: true })).toBeVisible();
  await expect(page.locator('.answer .error')).toHaveCount(0);
});

test('a screen action runs and Esc undoes it', async ({ page }) => {
  await page.keyboard.press('/');
  await page.getByLabel('Question').fill('Show me List');
  await page.keyboard.press('Enter');
  await expect.poll(async () => (await chatState(page)).bodies.length).toBe(1);

  await chatEvent(page, 'started', { conversationId: CONVERSATION });
  await chatEvent(page, 'tool', { name: 'focus_node', nodeIds: ['method:List'], isError: false });
  await chatEvent(page, 'action', { type: 'focus_node', args: { nodeId: 'method:List' } });
  await chatEvent(page, 'done', {
    conversationId: CONVERSATION,
    usage: { inputTokens: 1, outputTokens: 1 },
  });
  await page.evaluate(() =>
    (window as unknown as { __chat: { close: () => void } }).__chat.close(),
  );

  await expect(page.locator('.crumb')).toHaveText('Focused on List');
  // The node detail shows the selected node, and the next question carries it as context.
  await expect(page.locator('app-node-panel .name')).toHaveText('List');
  await expect(page.locator('.context')).toHaveText('about List');

  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await page.keyboard.press('Escape');
  await expect(page.locator('.crumb')).not.toHaveText(/Focused on/);
});

test('a missing model says so and links Owners to settings', async ({ page }) => {
  await page.keyboard.press('/');
  await page.getByLabel('Question').fill('Hello');
  await page.keyboard.press('Enter');
  await expect.poll(async () => (await chatState(page)).bodies.length).toBe(1);
  await chatEvent(page, 'error', {
    code: 'model.not_configured',
    message: 'No model is set up for this project yet.',
  });
  await page.evaluate(() =>
    (window as unknown as { __chat: { close: () => void } }).__chat.close(),
  );

  await expect(page.getByRole('link', { name: 'Set up a model' })).toHaveAttribute(
    'href',
    /\/settings\/model\?project=/,
  );
});
