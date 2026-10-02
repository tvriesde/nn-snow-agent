import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseModels, parseChatResult, type AnswerProcessing } from '../src/contracts';
import { processingLines } from '../src/processing';
import { sendChat } from '../src/api';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { ModelSelector } from '../src/ModelSelector';

const catalog = { defaultModelId: 'gpt-5-nano', models: [{ id: 'gpt-5-nano', label: 'GPT-5 nano' }, { id: 'gpt-6-luna', label: 'GPT-6 luna' }] };
const processing: AnswerProcessing = {
  elapsedMilliseconds: 1234, selectedModelId: 'gpt-6-luna', selectedModelLabel: 'GPT-6 luna',
  modelInvoked: true, modelCalls: 2, providerModel: 'gpt-6-luna', tokens: { input: 300, output: 60, total: 360, cachedInput: 30, reasoning: 15 },
  tokensUnavailableReason: null, estimatedModelCost: null, costUnavailableReason: 'Cache-write counts unavailable.',
};
const answer = { conversationId: 'one', answer: 'Test', knowledgeSources: [], azureEvidence: [], warnings: [] };
test('accessible model dropdown renders both choices, current selection and disabled loading state', () => {
  const render = (disabled: boolean, models = catalog.models) => renderToStaticMarkup(React.createElement(ModelSelector,
    { models, selected: 'gpt-6-luna', onChange: () => {}, disabled }));
  const html = render(false);
  assert.match(html, /aria-label="Model for your next question"/);
  assert.match(html, /<option value="gpt-5-nano">GPT-5 nano<\/option>/);
  assert.match(html, /<option value="gpt-6-luna" selected="">GPT-6 luna<\/option>/);
  assert.doesNotMatch(html, / disabled=/);
  assert.match(render(true, []), / disabled=""/);
  assert.match(html, /without a model/);
});
test('catalog requires unique valid IDs and a real default, accepts explicitly empty catalog', () => {
  assert.deepEqual(parseModels(catalog), catalog);
  assert.deepEqual(parseModels({ defaultModelId: null, models: [] }), { defaultModelId: null, models: [] });
  for (const value of [
    { ...catalog, defaultModelId: 'unknown' },
    { ...catalog, models: [catalog.models[0], catalog.models[0]] },
    { defaultModelId: '', models: [{ id: '', label: 'Empty ID' }] },
    { defaultModelId: 'a', models: [{ id: 'a', label: ' ' }] },
    { defaultModelId: undefined, models: [] },
  ]) assert.throws(() => parseModels(value), /invalid model catalog/);
});
test('processing rejects partial contradictory or invented counts and renders only supplied values', () => {
  assert.equal(parseChatResult({ ...answer, processing }).processing, processing);
  for (const p of [
    { ...processing, elapsedMilliseconds: NaN }, { ...processing, modelInvoked: false },
    { ...processing, modelCalls: -1 }, { ...processing, tokens: { ...processing.tokens, total: 123 } },
    { ...processing, tokens: { ...processing.tokens, cachedInput: 301 } },
    { ...processing, tokens: { ...processing.tokens, reasoning: 61 } },
    { ...processing, modelCalls: 0, modelInvoked: false },
  ]) assert.throws(() => parseChatResult({ ...answer, processing: p }), /invalid/);
  const lines = processingLines(processing).join('\n');
  assert.match(lines, /1.23 s/);
  assert.match(lines, /2 model calls/);
  assert.match(lines, /360 total/);
  assert.match(lines, /Reasoning: 15 tokens \(included in output\)/);
  assert.match(lines, /Cost estimate unavailable: Cache-write/);
  const noModel = { ...processing, modelInvoked: false, modelCalls: 0, providerModel: null, tokens: null, tokensUnavailableReason: 'No model invoked.' };
  assert.equal(parseChatResult({ ...answer, processing: noModel }).processing, noModel);
  assert.doesNotMatch(processingLines(noModel).join('\n'), /Tokens across|USD 0/);
});
test('per-question selection goes to the protected API with existing conversation unchanged', async () => {
  const original = globalThis.fetch;
  let body: unknown;
  globalThis.fetch = async (_input, init) => {
    body = JSON.parse(String(init?.body));
    return new Response(JSON.stringify({ ...answer, processing }));
  };
  try {
    await sendChat({ config: { apiBaseUrl: 'https://api.example', entraTenantId: 'tenant', entraClientId: 'client', entraApiScope: 'scope' }, token: async () => 'test-only' }, 'Question', 'existing', 'gpt-6-luna');
    assert.deepEqual(body, { message: 'Question', conversationId: 'existing', modelId: 'gpt-6-luna' });
  } finally { globalThis.fetch = original; }
});
