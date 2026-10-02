import React from 'react';
import { StyleSheet, Text, View, useWindowDimensions } from 'react-native';
import { agentSettings, agentTools, applicationAliases, identityBoundaries, indexingFlow, requestFlow } from './architecture';
import { ArchitectureDiagram } from './ArchitectureDiagram';

const c = (name: string) => `var(--cp-${name})`;
const font = 'Helvetica, Arial, sans-serif';

function Flow({ title, steps }: { title: string; steps: readonly { title: string; detail: string }[] }) {
  return <View style={s.section}>
    <Text accessibilityRole="header" style={s.heading}>{title}</Text>
    <View style={s.flow}>{steps.map((step, index) => <View key={step.title} style={s.step}>
      <View style={s.stepHeader}>
        <View style={s.number}><Text style={s.numberText}>{index + 1}</Text></View>
        <Text style={s.title}>{step.title}</Text>
      </View>
      <Text style={s.body}>{step.detail}</Text>
    </View>)}</View>
  </View>;
}

export function ArchitecturePage() {
  const { width } = useWindowDimensions();
  const narrow = width < 760;
  return <View style={s.section}>
    <View style={s.notice}>
      <Text style={s.title}>A transparent demo, not a live estate inventory.</Text>
      <Text style={s.body}>This page describes the deployed demo design and configuration reference. It does not query Azure or expose keys, tenant/subscription/client identifiers, private resource IDs or raw telemetry. Inspect chat evidence for actual observations.</Text>
    </View>
    <View style={s.section}>
      <Text accessibilityRole="header" style={s.heading}>Architecture at a glance</Text>
      <ArchitectureDiagram />
    </View>
    <Flow title="How an employee question becomes an answer" steps={requestFlow} />
    <View style={s.branches}>
    <View style={[s.branch, narrow && s.singleColumn]}>
        <Text style={s.eyebrow}>KNOWLEDGE PATH</Text>
        <Text style={s.title}>SearchKnowledge → Azure AI Search</Text>
        <Text style={s.body}>Synthetic ServiceNow guidance can explain known workarounds. It cannot establish that a live application is currently down.</Text>
      </View>
      <View style={[s.branch, narrow && s.singleColumn]}>
        <Text style={s.eyebrow}>APPLICATION HEALTH SKILL</Text>
        <Text style={s.title}>GetApplicationHealth → azure-health-model-state → Azure MCP</Text>
        <Text style={s.body}>The packaged skill resolves an application tag or health model and reads its evaluated healthState. Healthy, Degraded, Unhealthy or Unknown comes from the model, never from infrastructure availability, deployment success or missing alerts.</Text>
      </View>
      <View style={[s.branch, narrow && s.singleColumn]}>
        <Text style={s.eyebrow}>LIVE EVIDENCE PATH</Text>
        <Text style={s.title}>InvestigateAzure → Azure MCP → Azure Monitor / Resource Health</Text>
        <Text style={s.body}>Read-only observations have an application scope, timestamp and observation window. Missing telemetry is an evidence gap, not proof of health.</Text>
      </View>
    </View>
    <View style={s.section}>
      <Text accessibilityRole="header" style={s.heading}>How the agent is configured</Text>
      <Text style={s.body}>The model selects between three functions. Application-health questions must invoke the packaged health-model skill. Clear authorized read requests need no extra confirmation; ambiguous application names require clarification. Guarded tools, not the model, determine Azure scope and arguments.</Text>
      <View style={s.grid}>{agentSettings.map(setting => <View key={setting.label} style={[s.card, narrow && s.singleColumn]}>
        <Text style={s.eyebrow}>{setting.label.toUpperCase()}</Text>
        <Text selectable style={s.title}>{setting.value}</Text>
        <Text style={s.body}>{setting.detail}</Text>
      </View>)}</View>
    </View>
    <View style={s.section}>
      <Text accessibilityRole="header" style={s.heading}>Tools and allowed investigations</Text>
      <Text style={s.body}>Azure MCP is pinned to 3.0.0-beta.49 (beta). It uses stdio and runs in read-only mode with proxy tools disabled. Only six native commands are exposed: four scoped infrastructure evidence commands and two health-model commands. The skill searches only the configured subscription and does not disclose unmatched estate inventories.</Text>
      <View style={s.aliasCard}>
        <Text style={s.title}>Exact live application aliases</Text>
        {applicationAliases.map(alias => <Text key={alias} selectable style={s.body}>{alias}</Text>)}
        <Text style={s.caption}>Claims Workbench, Broker Portal and the other insurer applications are fictional knowledge scenarios, not live Azure mappings.</Text>
        <Text style={s.body}>Application health: employee-it-helpdesk (application tag). Other application/tag queries require an exact matching health model in the configured subscription. An absent model means health cannot be determined.</Text>
      </View>
      {agentTools.map(tool => <View key={`${tool.name}-${tool.operation}`} style={s.tool}>
        <Text style={s.title}>{tool.name} · {tool.operation}</Text>
        {'command' in tool && <Text selectable style={s.code}>{tool.command}</Text>}
        <Text style={s.body}>{tool.detail}</Text>
      </View>)}
    </View>
    <Flow title="How ServiceNow-shaped data reaches the index" steps={indexingFlow} />
    <View style={s.section}>
      <Text accessibilityRole="header" style={s.heading}>Identity and trust boundaries</Text>
      <View style={s.grid}>{identityBoundaries.map(boundary => <View key={boundary.title} style={[s.card, narrow && s.singleColumn]}>
        <Text style={s.title}>{boundary.title}</Text>
        <Text style={s.body}>{boundary.detail}</Text>
      </View>)}</View>
    </View>
    <View style={s.notice}>
      <Text accessibilityRole="header" style={s.heading}>Minimum-cost hosting, honest limitations</Text>
      <Text style={s.body}>Frontend and backend share Free F1 hosting; Search is Free. Storage, Key Vault, on-demand Functions, telemetry and GPT tokens can incur usage charges. No paid semantic ranking, always-ready Function instances or HTTP probes are enabled.</Text>
      <Text style={s.body}>Free apps can cold-start or reach daily CPU limits and have no uptime SLA. Resource Health may return Unknown. Model answers remain guidance; check sources and live observations, and escalate urgent issues through your IT support channel.</Text>
    </View>
  </View>;
}

const s = StyleSheet.create({
  section: { gap: 20 },
  heading: { fontFamily: font, fontSize: 28, lineHeight: 32, fontWeight: '700', color: c('text-strong') },
  title: { fontFamily: font, fontSize: 18, lineHeight: 26, fontWeight: '700', color: c('text-strong'), flexShrink: 1 },
  body: { fontFamily: font, fontSize: 16, lineHeight: 24, color: c('text') },
  caption: { fontFamily: font, fontSize: 14, lineHeight: 21, color: c('text-muted') },
  eyebrow: { fontFamily: font, fontSize: 12, lineHeight: 18, fontWeight: '700', letterSpacing: 0.5, color: c('text-muted') },
  code: { fontFamily: 'Consolas, "Courier New", Courier, monospace', fontSize: 12, lineHeight: 20, color: c('text-soft'), wordBreak: 'break-all' } as never,
  notice: { padding: 24, gap: 12, borderRadius: 4, borderLeftWidth: 4, borderColor: c('brand'), backgroundColor: c('bg') },
  flow: { gap: 12, borderLeftWidth: 3, borderColor: c('brand'), paddingLeft: 16 },
  step: { padding: 20, gap: 12, borderRadius: 4, backgroundColor: c('surface'), boxShadow: c('shadow') } as never,
  stepHeader: { flexDirection: 'row', gap: 12, alignItems: 'center' },
  number: { width: 32, height: 32, flexShrink: 0, borderRadius: 16, backgroundColor: c('accent-soft'), alignItems: 'center', justifyContent: 'center' },
  numberText: { fontFamily: font, fontSize: 16, fontWeight: '700', color: c('accent-text') },
  grid: { flexDirection: 'row', flexWrap: 'wrap', gap: 16 },
  card: { flexGrow: 1, flexShrink: 1, flexBasis: 320, minWidth: 0, padding: 24, gap: 12, borderRadius: 4, backgroundColor: c('surface'), boxShadow: c('shadow') } as never,
  singleColumn: { flexBasis: '100%', width: '100%' },
  branches: { flexDirection: 'row', flexWrap: 'wrap', gap: 16 },
  branch: { flexGrow: 1, flexShrink: 1, flexBasis: 320, minWidth: 0, padding: 24, gap: 12, borderRadius: 4, borderTopWidth: 4, borderColor: c('brand'), backgroundColor: c('bg') },
  aliasCard: { padding: 20, gap: 8, borderRadius: 4, borderWidth: 1, borderColor: c('border'), backgroundColor: c('bg-elevated') },
  tool: { padding: 20, gap: 8, borderBottomWidth: 1, borderColor: c('border') },
});
