import React from 'react';
import { StyleSheet, Text, View } from 'react-native';
import type { AnswerProcessing } from './contracts';
import { processingLines } from './processing';

export function ProcessingDetails({ processing }: { processing?: AnswerProcessing | null }) {
  return <View style={s.panel}>
    <Text style={s.heading}>Answer processing</Text>
    {(processing ? processingLines(processing) : ['Processing details were not returned for this answer.']).map((line, index) =>
      <Text selectable key={index} style={s.line}>{line}</Text>)}
  </View>;
}
const s = StyleSheet.create({
  panel: { gap: 8, paddingVertical: 16, borderTopWidth: 1, borderColor: 'var(--cp-border)' },
  heading: { fontSize: 14, fontWeight: '700', color: 'var(--cp-text)' },
  line: { fontSize: 13, lineHeight: 20, color: 'var(--cp-text-muted)' },
});
