import type { AnswerProcessing } from './contracts';

export function processingLines(p: AnswerProcessing): string[] {
  const lines = [`Server processing: ${(p.elapsedMilliseconds / 1000).toFixed(2)} s (excludes browser/network time)`,
    p.modelInvoked
      ? `Selected model: ${p.selectedModelLabel} · ${p.modelCalls} model call${p.modelCalls === 1 ? '' : 's'}${p.providerModel ? ` · Provider model: ${p.providerModel}` : ''}`
      : `Selected model: ${p.selectedModelLabel} · No model invoked`];
  if (p.tokens) {
    lines.push(`Tokens across all model calls: ${p.tokens.input.toLocaleString()} input · ${p.tokens.output.toLocaleString()} output · ${p.tokens.total.toLocaleString()} total`);
    if (p.tokens.cachedInput !== null) lines.push(`Cached input: ${p.tokens.cachedInput.toLocaleString()} tokens (included in input)`);
    if (p.tokens.reasoning !== null) lines.push(`Reasoning: ${p.tokens.reasoning.toLocaleString()} tokens (included in output)`);
  } else lines.push(p.tokensUnavailableReason ?? 'Token usage unavailable.');
  if (p.estimatedModelCost) {
    lines.push(`Estimated model-token cost: USD ${p.estimatedModelCost.amount.toFixed(8)}`,
      p.estimatedModelCost.scope, `Prices as of ${p.estimatedModelCost.pricesAsOf}; source: ${p.estimatedModelCost.pricingSource}`);
  } else lines.push(`Cost estimate unavailable: ${p.costUnavailableReason ?? 'Incomplete pricing or billing breakdown.'}`);
  return lines;
}
