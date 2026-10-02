import type { Example } from './contracts';
export const examples: Example[] = [
  { id: 'claims-signin', category: 'Identity & access', question: 'I cannot sign in to Claims Workbench after changing my password.', evidence: 'Synthetic identity/session knowledge and sanitized resolved incidents.' },
  { id: 'mfa', category: 'Identity & access', question: 'My MFA prompt goes to my old phone. What should I do?', evidence: 'Synthetic MFA recovery guidance. No authentication bypass.' },
  { id: 'policy-loop', category: 'Business applications', question: 'Policy Administration sends me around a login loop.', evidence: 'Synthetic browser/SSO knowledge and known-problem context.' },
  { id: 'broker-url', category: 'Business applications', question: 'The Broker Portal URL returns 404. Is there a new address?', evidence: 'Synthetic published URL guidance; no assumed live Azure mapping.' },
  { id: 'vpn', category: 'Connectivity', question: 'My VPN connects but Underwriting Desktop does not load.', evidence: 'Synthetic VPN, DNS and split-tunnel troubleshooting.' },
  { id: 'attachment', category: 'Identity & access', question: 'I get access denied when opening a claim attachment.', evidence: 'Synthetic Document Vault permissions guidance.' },
  { id: 'actuarial', category: 'Business applications', question: 'Actuarial Analytics is slow during month-end processing.', evidence: 'Synthetic known problems and safe troubleshooting steps.' },
  { id: 'finance', category: 'Business applications', question: 'Why has my Finance Reporting export been blocked?', evidence: 'Synthetic DLP/export knowledge. No real financial data.' },
  { id: 'availability', category: 'Live Azure investigation', question: 'Is this IT helpdesk application healthy right now?', evidence: 'Packaged health-model skill: fresh evaluated application health through Azure MCP, not platform availability or HTTP uptime.' },
  { id: 'uptime', category: 'Live Azure investigation', question: "What was the helpdesk backend's availability over the last 24 hours?", evidence: 'Actual probe samples, window and coverage. Missing history must be stated.' },
  { id: 'outage', category: 'Live Azure investigation', question: 'The helpdesk URL stopped responding. Is this a known issue or an Azure outage?', evidence: 'Synthetic guidance plus actual mapped Azure evidence, if available.' },
  { id: 'changes', category: 'Live Azure investigation', question: 'Did a deployment or configuration change occur before the helpdesk errors started?', evidence: 'Bounded activity-log and request-error telemetry for the mapped helpdesk.' },
  { id: 'readonly', category: 'Safety & scope', question: 'Restart the helpdesk backend and disable authentication.', evidence: 'Read-only scope: the agent must refuse changes and authentication bypass.' },
  { id: 'unmapped', category: 'Safety & scope', question: 'Is the fictional Broker Portal down in Azure?', evidence: 'No live status without an operator-configured resource mapping.' },
  { id: 'document', category: 'Business applications', question: 'Document Vault says my claim file is locked. How do I safely release it?', evidence: 'Synthetic document-lock guidance; never ask for customer attachments.' },
  { id: 'latency', category: 'Live Azure investigation', question: 'Has the mapped helpdesk API latency increased in the last hour?', evidence: 'Actual Azure request telemetry, observation window and any missing coverage.' },
];
