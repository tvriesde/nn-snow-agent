export type RuntimeConfig = { apiBaseUrl: string; entraTenantId: string; entraClientId: string; entraApiScope: string };
export type KnowledgeSource = { id: string; number: string; title: string; snippet: string; application: string };
export type AzureEvidence = { resourceId: string; observedAt: string; window: string; summary: string };
export type ChatResult = { conversationId: string; answer: string; knowledgeSources: KnowledgeSource[]; azureEvidence: AzureEvidence[]; warnings: string[] };
export type Example = { id: string; category: string; question: string; evidence: string };
const isRecord = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null && !Array.isArray(value);
const strings = (value: unknown, keys: string[]) => isRecord(value) && keys.every(key => typeof value[key] === 'string');
export function validateConfig(value: unknown): RuntimeConfig {
  const keys = ['apiBaseUrl', 'entraTenantId', 'entraClientId', 'entraApiScope'];
  if (!strings(value, keys)) throw new Error('Public runtime configuration is missing or invalid.');
  const config = value as RuntimeConfig;
  const missing = keys.filter(key => !config[key as keyof RuntimeConfig].trim());
  if (missing.length) throw new Error(`Deployment configuration missing: ${missing.join(', ')}. Contact your IT administrator.`);
  const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  if (!guid.test(config.entraTenantId) || !guid.test(config.entraClientId)) throw new Error('Entra tenant and SPA client IDs must be single-tenant registration GUIDs.');
  if (!/^api:\/\/[0-9a-f-]{36}\/Helpdesk\.Access$/i.test(config.entraApiScope) || !guid.test(config.entraApiScope.slice(6, 42))) throw new Error('Entra API scope must be api://<backend client ID>/Helpdesk.Access.');
  let url: URL;
  try { url = new URL(config.apiBaseUrl); } catch { throw new Error('API base URL must be an absolute HTTPS URL.'); }
  if (url.username || url.password || url.search || url.hash || (url.protocol !== 'https:' && !(url.protocol === 'http:' && ['localhost', '127.0.0.1'].includes(url.hostname)))) throw new Error('API base URL must use HTTPS (HTTP is allowed only for localhost) without credentials, query, or fragment.');
  return { ...config, apiBaseUrl: config.apiBaseUrl.replace(/\/+$/, '') };
}
export function parseChatResult(value: unknown): ChatResult {
  if (!strings(value, ['conversationId', 'answer'])) throw new Error('Backend returned an invalid chat response.');
  const result = value as ChatResult;
  if (!result.conversationId || !result.answer || !Array.isArray(result.knowledgeSources) || !result.knowledgeSources.every(v => strings(v, ['id', 'number', 'title', 'snippet', 'application'])) ||
    !Array.isArray(result.azureEvidence) || !result.azureEvidence.every(v => strings(v, ['resourceId', 'observedAt', 'window', 'summary'])) ||
    !Array.isArray(result.warnings) || !result.warnings.every(v => typeof v === 'string')) throw new Error('Backend returned incomplete evidence or an invalid chat response.');
  return result;
}
export function parseExamples(value: unknown): Example[] {
  if (!Array.isArray(value) || !value.every(v => strings(v, ['id', 'category', 'question', 'evidence']))) throw new Error('Backend returned an invalid examples response.');
  return value;
}
export function errorMessage(error: unknown): string { return error instanceof Error ? error.message : 'An unexpected error occurred. Please try again.'; }
export function formatObservation(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? `Invalid observation time: ${value}` : date.toISOString().replace('T', ' ').replace('.000Z', ' UTC');
}
