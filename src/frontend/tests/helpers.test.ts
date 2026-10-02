import { test } from 'node:test';
import assert from 'node:assert/strict';
import { validateConfig, parseChatResult, parseExamples, errorMessage, formatObservation } from '../src/contracts';
import { examples } from '../src/examples';
import { apiRequest, sendChat } from '../src/api';
import { pages, pageFromPath, pagePath } from '../src/navigation';
import { agentSettings, agentTools, applicationAliases, indexingFlow, requestFlow, identityBoundaries } from '../src/architecture';
import diagram from '../public/architecture.excalidraw.json';

test('architecture diagram has editable visible components and both evidence/indexing paths without private IDs', () => {
  assert.equal(diagram.type, 'excalidraw');
  assert.equal(new Set(diagram.elements.map(element => element.id)).size, diagram.elements.length);
  const shapes = diagram.elements.filter(element => element.type === 'rectangle');
  assert.deepEqual(shapes.map(element => element.id), [
    'entra', 'vault', 'employee', 'frontend', 'backend', 'model', 'search', 'mcp', 'monitor', 'blob', 'indexer',
  ]);
  for (const element of diagram.elements) {
    assert.ok(element.width >= 0 && element.height >= 0);
    if (element.type === 'text') {
      assert.ok(element.width > 0 && element.height > 0 && element.text);
      assert.equal(element.strokeColor, '#000000');
    }
    if (element.type === 'arrow') assert.ok(element.points && element.points.length >= 2);
  }
  const arrows = diagram.elements.filter(element => element.type === 'arrow').map(element => element.id);
  for (const path of ['signin', 'key', 'api', 'inference', 'knowledge', 'investigate', 'observations', 'batches', 'index']) {
    assert.ok(arrows.includes(path));
  }
  assert.doesNotMatch(JSON.stringify(diagram), /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|\/subscriptions\/|\.vault\.azure\.net|\.openai\.azure\.com/i);
});

test('architecture route supports direct links and navigation without changing existing routes', () => {
  assert.equal(pageFromPath('/'), 'chat');
  assert.equal(pageFromPath('/auth/callback'), 'chat');
  assert.equal(pageFromPath('/examples'), 'examples');
  assert.equal(pageFromPath('/architecture'), 'architecture');
  assert.equal(pageFromPath('/architecture/'), 'architecture');
  for (const page of pages) {
    assert.equal(pagePath(page.id), page.path);
    assert.equal(pageFromPath(page.path), page.id);
  }
  assert.equal(new Set(pages.map(page => page.path)).size, 3);
});

test('architecture reference explains real evidence, bounded model configuration and delta safety', () => {
  const text = JSON.stringify({ requestFlow, indexingFlow, agentSettings, agentTools, identityBoundaries });
  for (const requirement of ['PKCE', 'Key Vault', '512 MB', '15 minutes', 'checkpoint', 'idempotent',
    'GPT-5 nano', 'Low reasoning', '2,000', '4 model/tool-loop', '6 tool calls', 'Latest 4', '12-turn',
    'Strict JSON', 'missing buckets', 'uptime is unknown', 'not a hard OS security boundary']) {
    assert.ok(text.includes(requirement), `Missing architecture detail: ${requirement}`);
  }
  assert.equal(agentTools.filter(tool => 'command' in tool).length, 5);
  const health = agentTools.find(tool => tool.name === 'GetApplicationHealth');
  assert.ok(health && 'command' in health);
  assert.equal(health.command, 'monitor_healthmodels_list + monitor_healthmodels_get');
  for (const requirement of ['SKILL.md', 'azure-health-model-state', '20-model', 'healthState', 'Provisioning']) {
    assert.ok(text.includes(requirement), `Missing health skill detail: ${requirement}`);
  }
  assert.deepEqual(applicationAliases, ['Employee IT Helpdesk backend', 'Employee IT Helpdesk frontend']);
});

test('architecture documentation exposes no tenant, subscription, client or private resource identifiers', () => {
  const text = JSON.stringify({ requestFlow, indexingFlow, agentSettings, agentTools, applicationAliases, identityBoundaries });
  assert.doesNotMatch(text, /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i);
  assert.doesNotMatch(text, /\/subscriptions\/|\.vault\.azure\.net|\.openai\.azure\.com/i);
});
const config = { apiBaseUrl: 'https://helpdesk.example.test/', entraTenantId: '47c94d43-bd0b-4cc0-9c81-412496225c31', entraClientId: '11111111-1111-1111-1111-111111111111', entraApiScope: 'api://22222222-2222-2222-2222-222222222222/Helpdesk.Access' };
test('public configuration is validated and trailing slash normalized', () => {
  assert.equal(validateConfig(config).apiBaseUrl, 'https://helpdesk.example.test');
  assert.throws(() => validateConfig({ ...config, entraClientId: '' }), /missing/i);
  assert.throws(() => validateConfig({ ...config, entraTenantId: 'common' }), /single-tenant/i);
  assert.throws(() => validateConfig({ ...config, entraApiScope: 'User.Read' }), /scope/i);
  assert.throws(() => validateConfig({ ...config, apiBaseUrl: 'http://insecure.test' }), /HTTPS/);
  assert.throws(() => validateConfig({ ...config, apiBaseUrl: 'https://user:password@test.test' }), /credentials/);
  assert.equal(validateConfig({ ...config, apiBaseUrl: 'http://localhost:5000' }).apiBaseUrl, 'http://localhost:5000');
});
test('chat contract requires real answer and all evidence arrays', () => {
  const result = { conversationId: 'one', answer: '<script>not interpreted</script>', knowledgeSources: [], azureEvidence: [], warnings: [] };
  assert.deepEqual(parseChatResult(result), result);
  assert.throws(() => parseChatResult({ ...result, answer: '' }), /invalid/);
  assert.throws(() => parseChatResult({ ...result, azureEvidence: [{ summary: 'fake' }] }), /invalid/);
  assert.throws(() => parseChatResult({ answer: 'answer' }), /invalid/);
  assert.throws(() => parseChatResult({ ...result, warnings: [123] }), /invalid/);
});
test('examples cover employee issues, real Azure scope and unmapped refusals', () => {
  assert.ok(examples.length >= 14);
  assert.equal(new Set(examples.map(v => v.id)).size, examples.length);
  assert.equal(parseExamples(examples).length, examples.length);
  assert.ok(examples.some(v => v.id === 'unmapped' && v.evidence.includes('No live status')));
  assert.ok(examples.filter(v => v.category === 'Live Azure investigation').length >= 4);
  assert.ok(examples.some(v => v.id === 'readonly'));
  assert.throws(() => parseExamples([{ question: 'missing metadata' }]), /invalid/);
});
test('error and timestamp rendering do not imply invalid dates are real observations', () => {
  assert.equal(errorMessage(new Error('Service unavailable')), 'Service unavailable');
  assert.match(errorMessage(null), /unexpected/);
  assert.equal(formatObservation('2026-09-30T09:00:00Z'), '2026-09-30 09:00:00 UTC');
  assert.match(formatObservation('not a time'), /Invalid observation/);
});
test('chat transport sends only the employee access token and the exact contract', async () => {
  const originalFetch = globalThis.fetch;
  const auth = { config, token: async () => 'employee-test-token' };
  let requested = '';
  let sent: RequestInit | undefined;
  globalThis.fetch = async (input, init) => {
    requested = String(input); sent = init;
    return new Response(JSON.stringify({ conversationId: 'returned-id', answer: 'Test-only response', knowledgeSources: [], azureEvidence: [], warnings: [] }), { status: 200 });
  };
  try {
    assert.equal((await sendChat(auth, 'Question', 'existing-id')).conversationId, 'returned-id');
    assert.equal(requested, `${config.apiBaseUrl}/api/chat`);
    assert.equal(sent?.method, 'POST');
    assert.deepEqual(sent?.headers, { Authorization: 'Bearer employee-test-token', 'Content-Type': 'application/json' });
    assert.deepEqual(JSON.parse(String(sent?.body)), { message: 'Question', conversationId: 'existing-id' });
    assert.equal(sent?.credentials, 'omit');
  } finally { globalThis.fetch = originalFetch; }
});
test('unauthorized, backend failure, unreachable, timeout and invalid response fail explicitly', async () => {
  const originalFetch = globalThis.fetch;
  const auth = { config, token: async () => 'test-token' };
  try {
    for (const [status, pattern] of [[401, /authorization/], [403, /authorization/], [429, /request limit/], [503, /No agent answer/]] as const) {
      globalThis.fetch = async () => new Response('', { status });
      await assert.rejects(apiRequest(auth, '/api/chat', 'Question'), pattern);
    }
    globalThis.fetch = async () => { throw new TypeError('Network'); };
    await assert.rejects(apiRequest(auth, '/api/chat', 'Question'), /unreachable/);
    globalThis.fetch = async () => { throw new DOMException('Timeout', 'AbortError'); };
    await assert.rejects(apiRequest(auth, '/api/chat', 'Question'), /timed out/);
    globalThis.fetch = async () => new Response(JSON.stringify({ answer: 'Incomplete' }), { status: 200 });
    await assert.rejects(sendChat(auth, 'Question'), /invalid chat response/);
  } finally { globalThis.fetch = originalFetch; }
});
test('no authenticated fetch is made when token acquisition fails', async () => {
  const originalFetch = globalThis.fetch;
  let calls = 0;
  globalThis.fetch = async () => { calls++; return new Response(); };
  const auth = { config, token: async () => { throw new Error('Employee sign-in required'); } };
  try {
    await assert.rejects(apiRequest(auth, '/api/chat', 'Question'), /sign-in required/);
    assert.equal(calls, 0);
  } finally { globalThis.fetch = originalFetch; }
});
