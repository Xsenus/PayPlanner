#!/usr/bin/env node

import assert from 'node:assert/strict';
import { chromium } from '@playwright/test';

const appUrl = process.env.PAY1_APP_URL ?? 'http://localhost:5173';
const alphaEmail = process.env.PAY1_ALPHA_EMAIL;
const alphaPassword = process.env.PAY1_ALPHA_PASSWORD;
const betaEmail = process.env.PAY1_BETA_EMAIL;
const betaPassword = process.env.PAY1_BETA_PASSWORD;

if (![alphaEmail, alphaPassword, betaEmail, betaPassword].every(Boolean)) {
  throw new Error('Set PAY1_ALPHA_EMAIL, PAY1_ALPHA_PASSWORD, PAY1_BETA_EMAIL and PAY1_BETA_PASSWORD for a disposable local database.');
}

const browser = await chromium.launch({
  headless: true,
  ...(process.env.PAY1_CHROMIUM_PATH ? { executablePath: process.env.PAY1_CHROMIUM_PATH } : {}),
});
const faults = [];
const attachDiagnostics = (page, label) => {
  page.on('pageerror', (error) => faults.push(`${label}: ${error.message}`));
  page.on('console', (message) => {
    if (message.type() === 'error' && !/favicon/i.test(message.text())) faults.push(`${label}: ${message.text()}`);
  });
};

async function assertUniqueRenderedMessages(page) {
  const ids = await page.locator('[data-message-id]').evaluateAll((elements) => elements.map((element) => element.getAttribute('data-message-id')));
  assert.equal(new Set(ids).size, ids.length, 'The same chat message is rendered more than once');
}

async function login(context, label, email, password) {
  const page = await context.newPage();
  attachDiagnostics(page, label);
  await page.goto(`${appUrl}/?ppTab=messenger`);
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Пароль').fill(password);
  await page.getByRole('button', { name: 'Войти' }).click();
  await page.getByRole('heading', { name: 'Мессенджер', includeHidden: true }).waitFor({ state: 'attached', timeout: 30_000 });
  const releaseDialog = page.getByRole('dialog', { name: 'Что нового' });
  await releaseDialog.waitFor({ timeout: 5_000 }).catch(() => {});
  if (await releaseDialog.isVisible()) await releaseDialog.getByRole('button', { name: 'Понятно' }).click();
  return page;
}

try {
  const alphaContext = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const betaContext = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const [alpha, beta] = await Promise.all([
    login(alphaContext, 'alpha', alphaEmail, alphaPassword),
    login(betaContext, 'beta', betaEmail, betaPassword),
  ]);

  await beta.getByRole('button', { name: 'Архив', exact: true }).click();
  const archivedChatActions = beta.getByRole('button', { name: /Действия с чатом/ });
  if (await archivedChatActions.count()) {
    await archivedChatActions.first().click();
    await beta.getByRole('button', { name: 'Вернуть из архива' }).click();
  }
  await beta.getByRole('button', { name: 'Все', exact: true }).click();

  await alpha.getByRole('button', { name: 'Новый чат' }).click();
  await alpha.getByRole('button', { name: 'Личный диалог' }).click();
  await alpha.getByText(betaEmail).click();
  await alpha.getByRole('button', { name: 'Открыть чат' }).click();
  await alpha.getByRole('heading', { name: 'Новый чат' }).waitFor({ state: 'hidden' });
  await alpha.getByLabel('Сообщения чата').waitFor();

  const firstMessage = `Локальная проверка двух пользователей ${Date.now()}`;
  await alpha.getByPlaceholder('Напишите сообщение…').fill(firstMessage);
  await alpha.getByRole('button', { name: 'Отправить' }).click();
  await alpha.getByLabel('Сообщения чата').getByText(firstMessage).waitFor();
  await beta.locator('aside button').filter({ hasText: firstMessage }).first().click({ timeout: 10_000 });
  await beta.getByLabel('Сообщения чата').getByText(firstMessage).waitFor({ timeout: 10_000 });
  await assertUniqueRenderedMessages(alpha);
  await assertUniqueRenderedMessages(beta);
  console.log('PASS: direct message appeared in the second live browser session');

  const draft = `Черновик ${Date.now()}`;
  await alpha.getByPlaceholder('Напишите сообщение…').fill(draft);
  const savedDrafts = await alpha.evaluate(() => Object.entries(sessionStorage).filter(([key]) => key.startsWith('payplanner.chatDraft.v1.')));
  assert.ok(savedDrafts.some(([, value]) => value === draft), 'Draft was not saved before reload');
  await alpha.reload();
  await alpha.getByRole('heading', { name: 'Мессенджер' }).waitFor();
  await alpha.getByPlaceholder('Напишите сообщение…').waitFor();
  assert.equal(await alpha.getByPlaceholder('Напишите сообщение…').inputValue(), draft);
  console.log('PASS: draft survives page reload');

  await beta.getByRole('button', { name: /Действия с чатом/ }).first().click();
  await beta.getByRole('button', { name: 'В архив' }).click();
  await beta.getByRole('button', { name: 'Архив', exact: true }).click();
  await beta.getByLabel('Сообщения чата').getByText(firstMessage).waitFor();
  await alpha.getByPlaceholder('Напишите сообщение…').fill(`Ещё одно сообщение ${Date.now()}`);
  await alpha.getByRole('button', { name: 'Отправить' }).click();
  await assertUniqueRenderedMessages(alpha);
  await beta.getByRole('button', { name: 'Архив', exact: true }).click();
  assert.equal(await beta.getByRole('button', { name: /Действия с чатом/ }).count(), 1);
  console.log('PASS: archive is personal and the thread remains available');

  await beta.getByRole('button', { name: 'Все', exact: true }).click();
  const channelName = `Локальный канал ${Date.now()}`;
  await alpha.getByRole('button', { name: 'Новый чат' }).click();
  await alpha.getByPlaceholder('Название канала').fill(channelName);
  await alpha.getByText(betaEmail).click();
  await alpha.getByRole('button', { name: 'Открыть чат' }).click();
  await alpha.getByRole('heading', { name: 'Новый чат' }).waitFor({ state: 'hidden' });
  await beta.locator('aside').getByText(channelName).waitFor({ timeout: 10_000 });
  console.log('PASS: a newly created channel appears for the invited colleague without reload');

  const mobileContext = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
  const mobile = await login(mobileContext, 'mobile', alphaEmail, alphaPassword);
  await mobile.getByPlaceholder('Напишите сообщение…').waitFor();
  const hasHorizontalOverflow = await mobile.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 2);
  assert.equal(hasHorizontalOverflow, false, 'Messenger causes horizontal overflow on mobile viewport');
  console.log('PASS: mobile chat opens without page-wide horizontal overflow');

  assert.deepEqual(faults, [], 'Browser console or page errors');
  console.log('PASS: no browser console errors');
} finally {
  await browser.close();
}
