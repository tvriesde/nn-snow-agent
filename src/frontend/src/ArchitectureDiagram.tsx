import React, { useId } from 'react';
import diagram from '../public/architecture.excalidraw.json';

const c = (name: string) => `var(--cp-${name})`;

export function ArchitectureDiagram() {
  const id = useId();
  const arrowId = `${id}-arrow`;
  return <figure style={{ margin: 0 }}>
    <div role="region" aria-label="Scrollable architecture diagram" tabIndex={0}
      style={{ overflowX: 'auto', border: `1px solid ${c('border')}`, borderRadius: 16, background: c('surface') }}>
      <svg viewBox="0 0 1000 790" role="img" aria-labelledby={`${id}-title ${id}-description`}
        style={{ display: 'block', width: '100%', minWidth: 760, height: 'auto', fontFamily: '"Segoe UI", Aptos, Calibri, -apple-system, BlinkMacSystemFont, sans-serif' }}>
        <title id={`${id}-title`}>Employee IT Helpdesk architecture diagram</title>
        <desc id={`${id}-description`}>
          Employees sign in with Entra ID and use the Expo frontend to call the .NET backend over HTTPS with an employee token.
          The backend validates tokens and citations, uses a Key Vault-resolved key for Azure OpenAI GPT-5 nano,
          retrieves employee knowledge from Azure AI Search, and runs a pinned read-only Azure MCP subprocess with a dedicated managed identity
          to query Azure Monitor, Log Analytics, activity and Resource Health.
          Synthetic ServiceNow exports in private Blob Storage are sanitized by a scheduled .NET Function every 15 minutes
          and indexed with safe upserts, deletes and checkpoints. MCP runs inside the backend, not as a public service.
          Arrows show data exchange, not deployment or network isolation boundaries.
        </desc>
        <defs>
          <marker id={arrowId} viewBox="0 0 10 10" refX="9" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
            <path d="M 0 0 L 10 5 L 0 10 z" fill={c('text-muted')} />
          </marker>
        </defs>
        {diagram.elements.map(element => {
          if (element.type === 'rectangle') {
            return <rect key={element.id} x={element.x} y={element.y} width={element.width} height={element.height}
              rx="10" fill={c(element.id === 'backend' || element.id === 'mcp' || element.id === 'indexer' ? 'accent-soft' : 'bg-elevated')}
              stroke={c('border-strong')} strokeWidth="1.5" />;
          }
          if (element.type === 'arrow' && element.points) {
            return <polyline key={element.id} points={element.points.map(([x, y]) => `${element.x + x},${element.y + y}`).join(' ')}
              fill="none" stroke={c('text-muted')} strokeWidth="2"
              markerStart={element.startArrowhead ? `url(#${arrowId})` : undefined} markerEnd={`url(#${arrowId})`} />;
          }
          if (element.type === 'text' && element.text) {
            const centered = element.textAlign === 'center';
            return <text key={element.id} x={element.x + (centered ? element.width / 2 : 0)} y={element.y}
              fill={c('text')} fontSize={element.fontSize} textAnchor={centered ? 'middle' : 'start'} dominantBaseline="hanging">
              {element.text.split('\n').map((line, index) => <tspan key={line}
                x={element.x + (centered ? element.width / 2 : 0)} dy={index ? 24 : 0} fontWeight={index === 0 && centered ? 600 : 400}>{line}</tspan>)}
            </text>;
          }
          return null;
        })}
      </svg>
    </div>
    <figcaption style={{ marginTop: 12, color: c('text-muted'), fontSize: 13, lineHeight: '21px' }}>
      Request and response arrows connect the browser, frontend, backend and model. Employee tokens go only to the API;
      model keys remain server-side. MCP is a backend subprocess, not a separate hosted service.
      On smaller screens, scroll the diagram horizontally; the detailed flows below remain readable without scrolling.
      {' '}<a href="/architecture.excalidraw.json" download style={{ color: c('link') }}>Download editable diagram</a>
    </figcaption>
  </figure>;
}
