import React from 'react';
import type { ModelOption } from './contracts';

export function ModelSelector({ models, selected, onChange, disabled }: {
  models: ModelOption[]; selected: string; onChange: (id: string) => void; disabled: boolean;
}) {
  return <label style={{ display: 'flex', flexDirection: 'column', gap: 8, fontSize: 14, color: 'var(--cp-text)' }}>
    <span>Model for your next question</span>
    <select aria-label="Model for your next question" value={selected} disabled={disabled}
      onChange={event => onChange(event.target.value)}
      style={{ padding: 12, minHeight: 44, maxWidth: '100%', font: 'inherit', borderRadius: 4,
        border: '1px solid var(--cp-border-strong)', color: 'var(--cp-text)', background: 'var(--cp-surface)' }}>
      {models.length === 0 && <option value="">Sign in to load available models</option>}
      {models.map(model => <option key={model.id} value={model.id}>{model.label}</option>)}
    </select>
    <span style={{ color: 'var(--cp-text-muted)', fontSize: 13 }}>
      Selection applies to each new question. Standalone health checks use the skill without a model.
    </span>
  </label>;
}
