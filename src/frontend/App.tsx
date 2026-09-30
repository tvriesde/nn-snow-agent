import React, { useEffect, useRef, useState } from 'react';
import { ActivityIndicator, Pressable, ScrollView, StyleSheet, Text, TextInput, View, useWindowDimensions } from 'react-native';
import type { AccountInfo } from '@azure/msal-browser';
import { EmployeeAuth } from './src/auth';
import { loadExamples, sendChat } from './src/api';
import { errorMessage, formatObservation, validateConfig, type ChatResult, type Example } from './src/contracts';
import { examples } from './src/examples';
import { ArchitecturePage } from './src/ArchitecturePage';
import { pages, pageFromPath, pagePath, type Page } from './src/navigation';

const c = (name: string) => `var(--cp-${name})`;
const font = '"Segoe UI", Aptos, Calibri, -apple-system, BlinkMacSystemFont, sans-serif';
type Turn = { id: number; question: string; result?: ChatResult; error?: string };
function Button({ label, onPress, primary = false, disabled = false, small = false }: { label: string; onPress: () => void; primary?: boolean; disabled?: boolean; small?: boolean }) {
  return <Pressable accessibilityRole="button" accessibilityLabel={label} accessibilityState={{ disabled }} disabled={disabled} onPress={onPress}
    style={({ pressed }) => [s.button, primary && s.primary, small && s.smallButton, (disabled || pressed) && { opacity: 0.6 }]}>
    <Text style={[s.buttonText, primary && { color: c('accent-fg') }]}>{label}</Text>
  </Pressable>;
}
function Notice({ children, error = false }: { children: React.ReactNode; error?: boolean }) {
  return <View accessibilityRole={error ? 'alert' : undefined} accessibilityLiveRegion="polite" style={[s.notice, error && { borderColor: c('danger') }]}>
    <Text style={[s.body, error && { color: c('danger') }]}>{children}</Text>
  </View>;
}
function WorkspaceIllustration() {
  return <View accessibilityRole="image" accessibilityLabel="Illustration of a calm workspace with a laptop and a plant" style={s.illustration}>
    <View style={s.sun} />
    <View style={s.orbit} />
    <View style={s.illustrationNote}><Text style={s.illustrationNoteTitle}>A little help.</Text><Text style={s.illustrationNoteText}>A better day.</Text></View>
    <View style={s.laptop}>
      <View style={s.laptopToolbar}><View style={s.windowDot} /><View style={s.windowDot} /><View style={s.windowDot} /></View>
      <View style={s.laptopContent}>
        <View style={s.assistantIcon}><Text style={s.assistantIconText}>✓</Text></View>
        <Text style={s.laptopTitle}>Let's get you going.</Text>
        <View style={s.screenLine} /><View style={[s.screenLine, { width: '62%' }]} />
        <View style={s.screenAction}><Text style={s.screenActionText}>Here to help →</Text></View>
      </View>
    </View>
    <View style={s.laptopBase} />
    <View style={s.plantStem} /><View style={s.plantLeafLeft} /><View style={s.plantLeafRight} /><View style={s.plantPot} />
    <View style={s.desk} />
  </View>;
}
function Evidence({ result }: { result: ChatResult }) {
  const [expanded, setExpanded] = useState(false);
  return <View style={s.evidence}>
    <View style={s.rowWrap}>
      <Text style={s.tag}>{result.knowledgeSources.length} knowledge sources</Text>
      <Text style={s.tag}>{result.azureEvidence.length} Azure observations</Text>
      <Pressable accessibilityRole="button" accessibilityState={{ expanded }} onPress={() => setExpanded(!expanded)} style={s.sourceToggle}>
        <Text style={s.link}>{expanded ? 'Hide evidence details −' : 'Inspect evidence details +'}</Text>
      </Pressable>
    </View>
    {result.warnings.map((warning, index) => <Notice key={index}>Caution · {warning}</Notice>)}
    {expanded && <View style={s.stack}>
      <Text accessibilityRole="header" style={s.sectionTitle}>Synthetic knowledge</Text>
      <Text style={s.caption}>ServiceNow-shaped demo records, not a real insurer's ServiceNow instance.</Text>
      {!result.knowledgeSources.length && <Text style={s.body}>No supporting knowledge sources were returned.</Text>}
      {result.knowledgeSources.map((source, index) => <View key={`${source.id}-${index}`} style={s.sourceCard}>
        <Text style={s.eyebrow}>{source.number} · {source.application}</Text>
        <Text style={s.sectionTitle}>{source.title}</Text>
        <Text selectable style={s.body}>{source.snippet}</Text>
        <Text selectable style={s.mono}>Source ID: {source.id}</Text>
      </View>)}
      <Text accessibilityRole="header" style={s.sectionTitle}>Live Azure evidence</Text>
      <Text style={s.caption}>Backend-returned observations only. Resource state is not an uptime guarantee; sampled coverage may be incomplete.</Text>
      {!result.azureEvidence.length && <Text style={s.body}>No live Azure observations were returned. This is not evidence that an application is healthy.</Text>}
      {result.azureEvidence.map((evidence, index) => <View key={`${evidence.resourceId}-${index}`} style={s.sourceCard}>
        <Text selectable style={s.body}>{evidence.summary}</Text>
        <Text selectable style={s.mono}>{evidence.resourceId}</Text>
        <Text style={s.caption}>Observed: {formatObservation(evidence.observedAt)}</Text>
        <Text style={s.caption}>Observation window: {evidence.window}</Text>
      </View>)}
    </View>}
  </View>;
}
export default function App() {
  const { width } = useWindowDimensions();
  const wide = width >= 980;
  const [page, setPage] = useState<Page>(pageFromPath(window.location.pathname));
  const [auth, setAuth] = useState<EmployeeAuth | null>(null);
  const [account, setAccount] = useState<AccountInfo | null>(null);
  const [booting, setBooting] = useState(true);
  const [bootError, setBootError] = useState('');
  const [authError, setAuthError] = useState('');
  const [question, setQuestion] = useState('');
  const [turns, setTurns] = useState<Turn[]>([]);
  const [conversationId, setConversationId] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [catalog, setCatalog] = useState<Example[]>(examples);
  const [catalogStatus, setCatalogStatus] = useState('');
  const [catalogBusy, setCatalogBusy] = useState(false);
  const [filter, setFilter] = useState('All');
  const [search, setSearch] = useState('');
  const [theme, setTheme] = useState(document.documentElement.dataset.theme || 'light');
  const input = useRef<TextInput>(null);
  const scroll = useRef<ScrollView>(null);
  const busyRef = useRef(false);
  useEffect(() => {
    let active = true;
    (async () => {
      try {
        const response = await fetch('/runtime-config.json', { cache: 'no-store' });
        if (!response.ok) throw new Error(`Unable to load public runtime configuration (HTTP ${response.status}).`);
        const client = new EmployeeAuth(validateConfig(await response.json()));
        const user = await client.initialize();
        if (active) { setAuth(client); setAccount(user); }
      } catch (error) { if (active) setBootError(errorMessage(error)); }
      finally { if (active) setBooting(false); }
    })();
    const route = () => setPage(pageFromPath(window.location.pathname));
    window.addEventListener('popstate', route);
    return () => { active = false; window.removeEventListener('popstate', route); };
  }, []);
  const navigate = (destination: Page) => {
    window.history.pushState({}, '', pagePath(destination));
    setPage(destination); setTimeout(() => {
      const heading = document.getElementById('page-heading');
      heading?.setAttribute('tabindex', '-1'); heading?.focus();
    }, 0);
  };
  const selectExample = (example: Example) => {
    setQuestion(example.question); navigate('chat'); setTimeout(() => input.current?.focus(), 50);
  };
  const signIn = async () => {
    if (!auth) return;
    setAuthError('');
    try { await auth.signIn(); } catch (error) { setAuthError(errorMessage(error)); }
  };
  const send = async () => {
    const message = question.trim();
    if (!message || busyRef.current || !auth || !account) return;
    busyRef.current = true; setBusy(true);
    const id = Date.now();
    setTurns(previous => [...previous, { id, question: message }]); setQuestion('');
    try {
      const result = await sendChat(auth, message, conversationId);
      setConversationId(result.conversationId);
      setTurns(previous => previous.map(turn => turn.id === id ? { ...turn, result } : turn));
    } catch (error) {
      setTurns(previous => previous.map(turn => turn.id === id ? { ...turn, error: errorMessage(error) } : turn));
      setQuestion(message);
    } finally { busyRef.current = false; setBusy(false); }
  };
  const refreshExamples = async () => {
    if (!auth || !account || catalogBusy) return;
    setCatalogBusy(true); setCatalogStatus('');
    try {
      const remote = await loadExamples(auth);
      setCatalog([...examples, ...remote.filter(v => !examples.some(local => local.id === v.id))]);
      setCatalogStatus(`Loaded ${remote.length} backend examples. Example descriptions are expected evidence, not observations.`);
    } catch (error) { setCatalogStatus(`Backend examples unavailable: ${errorMessage(error)} Built-in questions remain available; they contain no agent answers.`); }
    finally { setCatalogBusy(false); }
  };
  const filtered = catalog.filter(v => (filter === 'All' || v.category === filter) && `${v.question} ${v.category}`.toLowerCase().includes(search.toLowerCase()));
  return <View style={s.app}>
    <View style={s.topbar}>
      <View style={[s.headerContent, !wide && { paddingHorizontal: 20 }]}>
      <Pressable accessibilityRole="link" accessibilityLabel="NN employee helpdesk home" {...{ href: '/' }} onPress={event => { event.preventDefault(); navigate('chat'); }} style={s.brand}>
        <View style={s.brandMark}><Text style={s.brandLetter}>nn</Text></View>
        <View><Text style={s.brandTitle}>Nationale-Nederlanden</Text><Text style={s.caption}>Employee IT · inspired demo</Text></View>
      </Pressable>
      <View style={s.rowWrap}>
        <Button label={theme === 'dark' ? 'Light theme' : 'Dark theme'} small onPress={() => { const next = theme === 'dark' ? 'light' : 'dark'; document.documentElement.dataset.theme = next; setTheme(next); }} />
        {account ? <><Text style={s.caption}>{account.name || account.username}</Text><Button label="Sign out" small onPress={() => { auth?.signOut().catch(error => setAuthError(errorMessage(error))); }} /></> :
          <Button label="Employee sign in" small primary disabled={!auth || booting} onPress={signIn} />}
      </View>
      </View>
    </View>
    <View style={s.navigation}>
      <View style={[s.navigationContent, !wide && { paddingHorizontal: 20 }]}>
          {pages.map(item => <Pressable key={item.id} {...{ href: item.path, 'aria-current': page === item.id ? 'page' : undefined }} accessibilityRole="link" accessibilityLabel={item.label} accessibilityState={{ selected: page === item.id }} onPress={event => { event.preventDefault(); navigate(item.id); }} style={[s.nav, page === item.id && s.navSelected]}>
            <Text style={[s.navText, page === item.id && { color: c('accent-text') }]}>{item.label}</Text>
          </Pressable>)}
          {wide && <Text style={s.navigationNote}>For employees. Here to help.</Text>}
      </View>
    </View>
    <View style={s.shell}>
      <ScrollView ref={scroll} style={s.main} contentContainerStyle={s.mainContent} keyboardShouldPersistTaps="handled">
        <View style={s.hero}>
          <View style={[s.heroContent, !wide && { flexDirection: 'column', padding: 24 }]}>
            <View style={[s.heroCopy, !wide && { width: '100%' }]}>
              <Text style={s.eyebrow}>NN · EMPLOYEE IT HELPDESK</Text>
              <Text nativeID="page-heading" accessibilityRole="header" style={[s.heading, !wide && { fontSize: 36, lineHeight: 43 }]}>{page === 'chat' ? <>Your work matters.{'\n'}<Text style={s.headingAccent}>We're here to help.</Text></> : page === 'examples' ? <>A good question.{'\n'}<Text style={s.headingAccent}>A great place to start.</Text></> : <>Behind the answer.{'\n'}<Text style={s.headingAccent}>Know how it works.</Text></>}</Text>
              <Text style={s.subtitle}>{page === 'chat' ? 'A sign-in problem? An application that won’t open? Find a clear next step, with IT knowledge and cloud evidence you can check.' : page === 'examples' ? 'Find inspiration for your IT question. From everyday access issues to application availability, we’ll help you get started.' : 'Explore the application architecture, the agent configuration and the boundaries that keep knowledge and live Azure evidence separate.'}</Text>
              <View style={s.rowWrap}>
                <Button label={page === 'chat' ? 'Ask a question →' : 'Back to helpdesk →'} primary onPress={() => { if (page !== 'chat') navigate('chat'); setTimeout(() => { document.getElementById('question-composer')?.scrollIntoView({ block: 'center' }); input.current?.focus(); }, 50); }} />
                {page === 'chat' && <Pressable accessibilityRole="link" {...{ href: '/examples' }} onPress={event => { event.preventDefault(); navigate('examples'); }} style={s.sourceToggle}><Text style={s.link}>Explore example questions →</Text></Pressable>}
              </View>
            </View>
            {wide && <WorkspaceIllustration />}
          </View>
        </View>
        <View style={s.content}>
          <View style={s.assuranceStrip}>
            <Text style={s.assuranceItem}>✓ Practical IT guidance</Text>
            <Text style={s.assuranceItem}>✓ Sources you can inspect</Text>
            <Text style={s.assuranceItem}>✓ Read-only Azure access</Text>
            <Text style={s.caption}>Synthetic data · demo environment</Text>
          </View>
          {booting && <View accessibilityLiveRegion="polite" style={s.rowWrap}><ActivityIndicator color={c('accent')} /><Text style={s.body}>Loading secure employee configuration…</Text></View>}
          {bootError && <Notice error>{bootError} Authentication has not been bypassed. Configure the deployment and reload this page.</Notice>}
          {authError && <Notice error>{authError}</Notice>}
          {!booting && !bootError && !account && <View style={s.signinCard}>
            <Text style={s.sectionTitle}>Your employee identity is required.</Text>
            <Text style={s.body}>Sign in through Microsoft Entra to ask questions. Your access token is used only for the helpdesk API; this app has no client secret.</Text>
            <View style={s.rowWrap}><Button label="Continue with Microsoft Entra" primary onPress={signIn} /></View>
          </View>}
          {page === 'chat' ? <>
            {turns.length === 0 && <View style={s.welcomeCard}>
              <Text accessibilityRole="header" style={s.welcomeTitle}>How can we help you today?</Text>
              <Text style={s.body}>From a sign-in loop to helpdesk availability, start with the application name, the error and when it began.</Text>
              <View style={[s.rowWrap, { alignItems: 'stretch' }]}>{[examples[0], examples[8], examples[11]].map(example =>
                <Pressable key={example.id} accessibilityRole="button" accessibilityLabel={`Use question: ${example.question}`} onPress={() => selectExample(example)} style={s.quickCard}>
                  <View style={s.quickIcon}><Text style={s.quickIconText}>{example.category === 'Identity & access' ? '↗' : '↔'}</Text></View>
                  <Text style={s.eyebrow}>{example.category}</Text><Text style={s.quickTitle}>{example.question}</Text><Text style={[s.link, { marginTop: 'auto' }]}>Ask this question →</Text>
                </Pressable>)}</View>
            </View>}
            {turns.length > 0 && <View style={s.rowWrap}><Text style={s.sectionTitle}>Your conversation</Text><Button label="New conversation" small disabled={busy} onPress={() => { setTurns([]); setConversationId(undefined); setQuestion(''); input.current?.focus(); }} /></View>}
            {turns.map(turn => <View key={turn.id} style={s.turn}>
              <View style={s.questionCard}><Text style={s.eyebrow}>YOU</Text><Text selectable style={s.body}>{turn.question}</Text></View>
              <View style={s.answerCard}><Text style={s.eyebrow}>EMPLOYEE IT ASSISTANT</Text>
                {turn.result ? <><Text selectable style={s.answer}>{turn.result.answer}</Text><Evidence result={turn.result} /></> :
                  turn.error ? <Notice error>{turn.error}</Notice> :
                    <View accessibilityLiveRegion="polite" style={s.rowWrap}><ActivityIndicator color={c('accent')} /><Text style={s.body}>Investigating knowledge and available evidence…</Text></View>}
              </View>
            </View>)}
            <View nativeID="question-composer" style={s.composer}>
              <Text style={s.eyebrow}>LET'S TAKE THE NEXT STEP</Text>
              <Text style={s.sectionTitle}>Ask your IT question</Text>
              <TextInput ref={input} accessibilityLabel="Your IT question" multiline maxLength={4000} editable={!busy} value={question} onChangeText={setQuestion} placeholder="For example: Claims Workbench shows access denied after sign-in…" placeholderTextColor={c('text-soft')} style={s.textarea} />
              <View style={s.composerFooter}><Text style={s.caption}>{question.length}/4000 · No policy, claim or personal information.</Text><Button label={busy ? 'Investigating…' : 'Send question →'} primary disabled={!account || !auth || busy || !question.trim()} onPress={() => { void send(); setTimeout(() => scroll.current?.scrollToEnd({ animated: false }), 100); }} /></View>
              <Text style={s.caption}>Enter adds a new line. Tab to Send and press Enter or Space to submit.</Text>
            </View>
          </> : page === 'architecture' ? <ArchitecturePage /> : <>
            <View style={s.rowWrap}><TextInput accessibilityLabel="Search example questions" value={search} onChangeText={setSearch} placeholder="Search questions…" placeholderTextColor={c('text-soft')} style={[s.search, { flexGrow: 1 }]} />
              <Button label={catalogBusy ? 'Loading examples…' : 'Load backend examples'} disabled={!account || catalogBusy} onPress={() => { void refreshExamples(); }} /></View>
            {catalogStatus && <Notice>{catalogStatus}</Notice>}
            <View style={s.rowWrap}>{['All', ...Array.from(new Set(catalog.map(v => v.category)))].map(category =>
              <Pressable key={category} accessibilityRole="button" accessibilityState={{ selected: filter === category }} onPress={() => setFilter(category)} style={[s.filter, category === filter && s.filterSelected]}><Text style={[s.caption, category === filter && { color: c('accent-text') }]}>{category}</Text></Pressable>)}</View>
            <Text accessibilityLiveRegion="polite" style={s.caption}>{filtered.length} question{filtered.length === 1 ? '' : 's'} · Evidence descriptions are expectations, not live results.</Text>
            <View style={s.grid}>{filtered.map(example => <Pressable key={example.id} accessibilityRole="button" accessibilityLabel={`Use question: ${example.question}`} onPress={() => selectExample(example)} style={[s.exampleCard, { width: width >= 760 ? '48.5%' : '100%' }]}>
              <Text style={s.eyebrow}>{example.category}</Text><Text style={s.sectionTitle}>{example.question}</Text><Text style={s.caption}>{example.evidence}</Text><Text style={s.link}>Use in chat →</Text>
            </Pressable>)}</View>
            {filtered.length === 0 && <Notice>No examples match. Try another search or category.</Notice>}
          </>}
          <View style={s.footer}>
            <Text style={s.sectionTitle}>Nationale-Nederlanden · Employee IT demo</Text>
            <Text style={s.caption}>An NN-inspired concept, not an official NN service. Original interface illustration; no customer or policy data.</Text>
            <Text style={s.caption}>Demo boundaries · Synthetic knowledge is not real operational history. Only explicitly mapped Azure resources can have live evidence. Missing telemetry is reported, never invented.</Text>
            <Text style={s.caption}>Free-tier services may cold start. Answers are guidance, not a service-level guarantee. Contact your IT support channel for urgent issues.</Text>
          </View>
        </View>
      </ScrollView>
    </View>
  </View>;
}
const s = StyleSheet.create({
  app: { flex: 1, minHeight: '100%', backgroundColor: c('surface') },
  topbar: { backgroundColor: c('surface'), alignItems: 'center' },
  headerContent: { width: '100%', maxWidth: 1200, flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', justifyContent: 'space-between', gap: 20, paddingHorizontal: 40, paddingVertical: 24 },
  brand: { flexDirection: 'row', gap: 12, alignItems: 'center' },
  brandMark: { width: 56, height: 56, borderRadius: 28, backgroundColor: c('brand'), alignItems: 'center', justifyContent: 'center' },
  brandLetter: { fontFamily: font, fontSize: 30, fontWeight: '700', color: c('accent-fg'), letterSpacing: -3, paddingRight: 3 },
  brandTitle: { fontFamily: font, color: c('text'), fontWeight: '700', fontSize: 20, lineHeight: 28 },
  shell: { flex: 1 },
  navigation: { backgroundColor: c('bg'), alignItems: 'center', borderBottomWidth: 1, borderColor: c('border') },
  navigationContent: { width: '100%', maxWidth: 1200, flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', gap: 28, paddingHorizontal: 40 },
  navigationNote: { fontFamily: font, fontSize: 14, color: c('text-muted'), marginLeft: 'auto' },
  nav: { minHeight: 64, borderBottomWidth: 3, borderColor: c('bg'), justifyContent: 'center' },
  navSelected: { borderColor: c('accent') },
  navText: { fontFamily: font, fontSize: 17, color: c('text'), fontWeight: '600' },
  main: { flex: 1 }, mainContent: { alignItems: 'center' },
  content: { width: '100%', maxWidth: 1168, gap: 28, paddingHorizontal: 24, paddingBottom: 40 },
  hero: { width: '100%', backgroundColor: c('bg'), alignItems: 'center' },
  heroContent: { width: '100%', maxWidth: 1200, padding: 40, flexDirection: 'row', alignItems: 'center', gap: 36 },
  heroCopy: { width: '53%', gap: 24, paddingVertical: 12 },
  heading: { fontFamily: font, fontSize: 46, lineHeight: 54, fontWeight: '600', color: c('text') },
  headingAccent: { color: c('accent-text') },
  subtitle: { fontFamily: font, fontSize: 18, lineHeight: 29, color: c('text-muted'), maxWidth: 540 },
  assuranceStrip: { paddingVertical: 20, borderBottomWidth: 1, borderColor: c('border'), flexDirection: 'row', flexWrap: 'wrap', justifyContent: 'space-between', gap: 16 },
  assuranceItem: { fontFamily: font, fontSize: 14, fontWeight: '600', color: c('text-muted') },
  illustration: { flex: 1, height: 340, minWidth: 320, position: 'relative' },
  sun: { position: 'absolute', width: 256, height: 256, borderRadius: 128, backgroundColor: c('accent-soft'), right: 0, top: 24 },
  orbit: { position: 'absolute', width: 292, height: 292, borderRadius: 146, borderWidth: 1, borderColor: c('accent'), right: 12, top: 8 },
  illustrationNote: { position: 'absolute', left: 0, top: 6, padding: 16, backgroundColor: c('surface'), borderRadius: 16, gap: 4, transform: [{ rotate: '-4deg' }] },
  illustrationNoteTitle: { fontFamily: font, fontSize: 17, fontWeight: '600', color: c('text') },
  illustrationNoteText: { fontFamily: font, fontSize: 17, color: c('accent-text') },
  laptop: { position: 'absolute', left: 32, bottom: 58, width: 260, height: 202, borderRadius: 16, borderWidth: 6, borderColor: c('text'), backgroundColor: c('surface'), overflow: 'hidden' },
  laptopToolbar: { height: 24, backgroundColor: c('surface-soft'), flexDirection: 'row', gap: 5, alignItems: 'center', paddingHorizontal: 10 },
  windowDot: { width: 5, height: 5, borderRadius: 3, backgroundColor: c('border-strong') },
  laptopContent: { padding: 16, gap: 10 },
  assistantIcon: { width: 28, height: 28, borderRadius: 14, backgroundColor: c('accent-soft'), alignItems: 'center', justifyContent: 'center' },
  assistantIconText: { fontFamily: font, color: c('accent-text'), fontSize: 18 },
  laptopTitle: { fontFamily: font, fontSize: 16, fontWeight: '600', color: c('text') },
  screenLine: { width: '86%', height: 5, backgroundColor: c('border'), borderRadius: 3 },
  screenAction: { alignSelf: 'flex-start', paddingHorizontal: 10, paddingVertical: 5, backgroundColor: c('accent'), borderRadius: 6 },
  screenActionText: { fontFamily: font, fontSize: 11, color: c('accent-fg'), fontWeight: '600' },
  laptopBase: { position: 'absolute', width: 292, height: 12, left: 16, bottom: 46, backgroundColor: c('text'), borderBottomLeftRadius: 12, borderBottomRightRadius: 12 },
  plantStem: { position: 'absolute', width: 4, height: 80, right: 28, bottom: 72, backgroundColor: c('accent-text') },
  plantLeafLeft: { position: 'absolute', width: 36, height: 58, borderTopLeftRadius: 36, borderBottomRightRadius: 36, right: 32, bottom: 116, backgroundColor: c('accent') },
  plantLeafRight: { position: 'absolute', width: 32, height: 50, borderTopRightRadius: 32, borderBottomLeftRadius: 32, right: 0, bottom: 140, backgroundColor: c('accent-text') },
  plantPot: { position: 'absolute', width: 56, height: 52, right: 2, bottom: 46, backgroundColor: c('surface'), borderWidth: 2, borderColor: c('border'), borderBottomLeftRadius: 16, borderBottomRightRadius: 16 },
  desk: { position: 'absolute', left: 0, right: 0, bottom: 40, height: 6, borderRadius: 3, backgroundColor: c('border-strong') },
  body: { fontFamily: font, fontSize: 16, lineHeight: 25, color: c('text') },
  answer: { fontFamily: font, fontSize: 16, lineHeight: 27, color: c('text') },
  caption: { fontFamily: font, fontSize: 13, lineHeight: 20, color: c('text-muted') },
  eyebrow: { fontFamily: font, fontSize: 11, lineHeight: 18, fontWeight: '700', letterSpacing: 1, color: c('text-muted') },
  sectionTitle: { fontFamily: font, fontSize: 18, lineHeight: 26, fontWeight: '600', color: c('text') },
  mono: { fontFamily: 'Consolas, "Courier New", Courier, monospace', fontSize: 12, lineHeight: 20, color: c('text-soft'), wordBreak: 'break-all' } as never,
  link: { fontFamily: font, color: c('accent-text'), fontSize: 15, lineHeight: 24, fontWeight: '600' },
  rowWrap: { flexDirection: 'row', flexWrap: 'wrap', gap: 12, alignItems: 'center' },
  stack: { gap: 12 },
  button: { minHeight: 48, paddingHorizontal: 20, paddingVertical: 12, borderWidth: 1, borderColor: c('border'), borderRadius: 10, backgroundColor: c('surface'), justifyContent: 'center', alignItems: 'center' },
  primary: { backgroundColor: c('accent'), borderColor: c('accent') },
  smallButton: { minHeight: 44, paddingHorizontal: 14, paddingVertical: 8 },
  buttonText: { fontFamily: font, fontWeight: '600', fontSize: 14, lineHeight: 20, color: c('text') },
  tag: { fontFamily: font, color: c('accent-text'), fontWeight: '600', fontSize: 11, lineHeight: 20, letterSpacing: 0.5, paddingHorizontal: 10, paddingVertical: 4, borderRadius: 10, backgroundColor: c('accent-soft') },
  notice: { padding: 16, borderWidth: 1, borderColor: c('border-strong'), borderRadius: 10, backgroundColor: c('bg-elevated') },
  signinCard: { padding: 24, gap: 16, borderWidth: 1, borderColor: c('border'), borderRadius: 16, backgroundColor: c('surface') },
  welcomeCard: { gap: 20, paddingVertical: 12 },
  welcomeTitle: { fontFamily: font, fontSize: 28, lineHeight: 36, fontWeight: '600', color: c('text') },
  quickCard: { flexGrow: 1, flexBasis: 240, padding: 24, gap: 16, borderWidth: 1, borderColor: c('border'), borderRadius: 16, backgroundColor: c('surface') },
  quickTitle: { fontFamily: font, fontSize: 18, lineHeight: 28, fontWeight: '600', color: c('text') },
  quickIcon: { width: 44, height: 44, borderRadius: 10, backgroundColor: c('accent-soft'), justifyContent: 'center', alignItems: 'center' },
  quickIconText: { fontFamily: font, fontSize: 26, color: c('accent-text') },
  turn: { gap: 12 },
  questionCard: { alignSelf: 'flex-end', maxWidth: '95%', padding: 20, gap: 8, borderRadius: 16, backgroundColor: c('accent-soft'), borderWidth: 1, borderColor: c('border') },
  answerCard: { padding: 24, gap: 16, borderWidth: 1, borderColor: c('border'), borderRadius: 16, backgroundColor: c('surface') },
  evidence: { gap: 16, borderTopWidth: 1, borderColor: c('border'), paddingTop: 16 },
  sourceToggle: { minHeight: 44, paddingVertical: 10 },
  sourceCard: { padding: 16, gap: 8, borderRadius: 10, borderWidth: 1, borderColor: c('border'), backgroundColor: c('bg-elevated') },
  composer: { padding: 24, gap: 16, borderTopWidth: 4, borderColor: c('accent'), borderRadius: 16, backgroundColor: c('bg') },
  textarea: { minHeight: 116, padding: 16, borderRadius: 10, borderWidth: 1, borderColor: c('border'), fontFamily: font, fontSize: 16, lineHeight: 24, backgroundColor: c('bg-elevated'), color: c('text'), textAlignVertical: 'top' },
  composerFooter: { flexDirection: 'row', flexWrap: 'wrap', gap: 12, alignItems: 'center', justifyContent: 'space-between' },
  search: { minHeight: 48, padding: 14, borderRadius: 10, borderWidth: 1, borderColor: c('border'), backgroundColor: c('surface'), fontFamily: font, fontSize: 15, color: c('text') },
  filter: { minHeight: 44, padding: 12, borderRadius: 10, borderWidth: 1, borderColor: c('border'), justifyContent: 'center' },
  filterSelected: { backgroundColor: c('accent-soft'), borderColor: c('accent-text') },
  grid: { flexDirection: 'row', flexWrap: 'wrap', gap: 16, justifyContent: 'space-between' },
  exampleCard: { padding: 24, gap: 16, borderRadius: 16, borderWidth: 1, borderColor: c('border'), backgroundColor: c('surface') },
  footer: { gap: 12, paddingTop: 24, borderTopWidth: 1, borderColor: c('border') },
});
