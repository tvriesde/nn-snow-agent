export type RuntimeConfig = { apiBaseUrl: string; entraTenantId: string; entraClientId: string; entraApiScope: string };
export type KnowledgeSource = { id: string; number: string; title: string; snippet: string; application: string };
export type AzureEvidence = { resourceId: string; observedAt: string; window: string; summary: string };
export type ModelOption = { id: string; label: string };
export type ModelCatalog = { defaultModelId: string | null; models: ModelOption[] };
export type TokenUsage = { input: number; output: number; total: number; cachedInput: number | null; reasoning: number | null };
export type ModelCostEstimate = { amount: number; currency: string; scope: string; pricingSource: string; pricesAsOf: string };
export type AnswerProcessing = {
  elapsedMilliseconds: number; selectedModelId: string; selectedModelLabel: string; modelInvoked: boolean; modelCalls: number;
  providerModel: string | null; tokens: TokenUsage | null; tokensUnavailableReason: string | null;
  estimatedModelCost: ModelCostEstimate | null; costUnavailableReason: string | null;
};
export type ChatResult = { conversationId: string; answer: string; knowledgeSources: KnowledgeSource[]; azureEvidence: AzureEvidence[]; warnings: string[]; processing?: AnswerProcessing | null };
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
    !Array.isArray(result.warnings) || !result.warnings.every(v => typeof v === 'string') ||
    (result.processing != null && !validProcessing(result.processing))) throw new Error('Backend returned incomplete evidence or an invalid chat response.');
  return result;
}
const count = (v: unknown): v is number => typeof v === 'number' && Number.isSafeInteger(v) && v >= 0;
const nullableString = (v: unknown) => v === null || typeof v === 'string';
function validProcessing(v: unknown): boolean {
  if (!isRecord(v) || !strings(v, ['selectedModelId', 'selectedModelLabel']) ||
    typeof v.elapsedMilliseconds !== 'number' || !Number.isFinite(v.elapsedMilliseconds) || v.elapsedMilliseconds < 0 ||
    typeof v.modelInvoked !== 'boolean' || !count(v.modelCalls) || v.modelInvoked !== (v.modelCalls > 0) ||
    !nullableString(v.providerModel) || !nullableString(v.tokensUnavailableReason) || !nullableString(v.costUnavailableReason)) return false;
  if (v.tokens !== null && (!isRecord(v.tokens) || !count(v.tokens.input) || !count(v.tokens.output) || !count(v.tokens.total) ||
    !v.modelInvoked || v.tokens.total !== v.tokens.input + v.tokens.output ||
    (v.tokens.cachedInput !== null && (!count(v.tokens.cachedInput) || v.tokens.cachedInput > v.tokens.input)) ||
    (v.tokens.reasoning !== null && (!count(v.tokens.reasoning) || v.tokens.reasoning > v.tokens.output)))) return false;
  if (v.estimatedModelCost !== null && (!isRecord(v.estimatedModelCost) ||
    !strings(v.estimatedModelCost, ['currency', 'scope', 'pricingSource', 'pricesAsOf']) ||
    typeof v.estimatedModelCost.amount !== 'number' || !Number.isFinite(v.estimatedModelCost.amount) || v.estimatedModelCost.amount < 0 ||
    v.estimatedModelCost.currency !== 'USD' || v.tokens === null || !v.modelInvoked)) return false;
  return true;
}
export function parseModels(value: unknown): ModelCatalog {
  const isModel = (v: unknown): v is ModelOption => isRecord(v) && typeof v.id === 'string' &&
    /^[A-Za-z0-9_-]{1,80}$/.test(v.id) && typeof v.label === 'string' && !!v.label.trim() && v.label.length <= 100;
  if (!isRecord(value) || !Array.isArray(value.models) || value.models.length > 8 || !value.models.every(isModel) ||
    !nullableString(value.defaultModelId) ||
    new Set(value.models.map(v => v.id)).size !== value.models.length ||
    (value.models.length === 0 ? value.defaultModelId !== null : !value.models.some(v => v.id === value.defaultModelId)))
    throw new Error('Backend returned an invalid model catalog.');
  return { defaultModelId: typeof value.defaultModelId === 'string' ? value.defaultModelId : null, models: value.models };
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
