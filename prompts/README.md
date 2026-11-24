# FlowCapture AI prompt pack (v1)

## Files

| File | Purpose |
|---|---|
| `common-rules.v1.md` | Shared rules: source-only facts, no invention, As-is vs requirement, privacy placeholders, language, ids, Mermaid, output. |
| `part-analysis.system.v1.md` | First analysis of ONE flow from its entries. |
| `reanalysis.system.v1.md` | Update an existing flow using edits (locks), answers and new entries. |
| `whole-analysis.system.v1.md` | Cross-department analysis: hand-offs, duplicates, conflicts. |
| `retry.validation-feedback.v1.md` | Appended on the single retry after a failed validation. |
| `schemas/flow.v1.json` | JSON Schema for one flow (part and re-analysis output; also the stored `FlowVersion.Content`). |
| `schemas/project-analysis.v1.json` | JSON Schema for the whole-project result. |
| `examples/mrd-003.user-message.txt` | Worked input (voice transcript + lead's note). |
| `examples/mrd-003.expected.json` | Expected output for that input, built from RGD-MRD-003. Validates against `flow.v1.json`. |

## Versioning and storage

- The files are the **seed**. On startup the server upserts each file into `PromptTemplate(Key, Version, Content, Sha256, IsActive)`. Key = file stem without `.vN` (e.g. `part-analysis.system`), Version = N.
- An admin can add a new version or switch the active one in the UI. Prompts are never edited in place. A new edit is a new version.
- Every `AnalysisRun` stores the `PromptTemplateId`s used and the SHA-256 of the fully composed system prompt, so any result can be traced to the exact text that produced it.

## Prompt composition (server, `PromptComposer`)

Placeholders in the templates are resolved at call time:

| Placeholder | Resolved to |
|---|---|
| `{{include:common-rules.v1}}` | Contents of that template. |
| `{{schema:flow.v1}}` / `{{schema:project-analysis.v1}}` | The schema JSON, minified. The `requirement.category` enum is **patched** with the active category codes from admin config, plus `To be confirmed`. |
| `{{example:mrd-003.user-message}}` / `{{example:mrd-003.expected}}` | Example files (kept out of the templates so they are not duplicated). |
| `{{category_definitions}}` | Admin-configured list, e.g. `CR = Change Request (change to existing BACKBONE behaviour); NR = New Requirement (capability that does not exist); ER = Enhancement (improvement to an existing capability)`. Defaults pending your confirmation. |
| `{{errors}}` | Validation messages (retry prompt only). |

The schema is also passed as the `input_schema` of a single tool (`submit_flow_analysis` or `submit_project_analysis`), with `$ref`/`$defs` inlined first. Current Claude models do not accept a forced `tool_choice`, so the call uses `tool_choice: auto`; the prompt says to answer only by calling the tool, and a reply with no tool call is reported as `ai_no_tool_call`. The schema text in the system prompt is for the model's understanding; the server validates every result regardless. Prompt caching is applied to the system prompt, which is large and identical across calls.

## User-message envelope

The server builds the user message. Entry and answer text is XML-escaped so it cannot close a tag.

### Part analysis
```xml
<analysis_request type="part" prompt="part-analysis.v1">
  <context>
    <department>MRD</department>
    <module_code>MRD</module_code>
    <flow_name>IP Case Sheet Receive in Department</flow_name>
    <next_requirement_number>1</next_requirement_number>
  </context>
  <entries>
    <entry ref="E1" kind="voice_transcript|text" language="en-IN" interviewee_role="..." captured_on="yyyy-MM-dd">MASKED TEXT</entry>
  </entries>
</analysis_request>
```
`ref` values (`E1`, `E2`...) are assigned per request; the server keeps the map `E# -> CaptureEntryId` and translates `sourceEntryIds` back to entry ids when saving. The hospital name, project code, person names and interviewee names are **not** sent. Only the interviewee's role/designation is.

### Re-analysis
Same as above with `type="reanalysis"` and extra blocks:
```xml
  <previous_result>{ ...flow.v1 JSON, including user edits... }</previous_result>
  <locked_items>["S2","BR1","purpose"]</locked_items>
  <user_answers>
    <answer ref="A1" question_id="Q1">First mapped ward</answer>
  </user_answers>
  <entries> ... <entry ref="E3" new="true" ...>...</entry> </entries>
```
All entries are sent, and `new="true"` marks ones added since the previous version. `next_requirement_number` continues after the highest existing `REQ`.

### Whole analysis
```xml
<analysis_request type="whole" prompt="whole-analysis.v1">
  <flow ref="F1" status="Confirmed" department="MRD" title="...">{ flow.v1 JSON }</flow>
  <flow ref="F2" status="Analysed" department="Billing" title="...">{ ... }</flow>
</analysis_request>
```
Whole analysis reads structured flows, not raw entries. This is far cheaper and keeps raw (PHI-risk) text out of the large call.

## Server-side checks on every result (`FlowResultValidator`)

1. **JSON Schema** validation (tool output is parsed from `tool_use.input`).
2. **Referential integrity**: every `next`, `yesPath.next`, `noPath.next`, `branchFrom`, `rejoinAt`, `appliesTo` and `reqId` points at an existing id or `END`; `S1` exists; every step reachable from `S1`; ids unique per type.
3. **Source integrity**: every `sourceEntryIds` ref exists in the request; items the AI creates cite at least one ref; `department` and `moduleCode` equal the context.
4. **No-blank rule**: no empty strings; scalars are never whitespace; unknown values are exactly `To be confirmed`.
5. **Gap/question integrity**: every question's `relatedGapId` exists. On re-analysis, answered questions are not re-emitted.
6. **Lock enforcement (re-analysis)**: for every locked id, the output item must deep-equal the item in `previous_result`. If not, the server **restores the user's version** and records a warning (never trusts the model with user edits). If a locked item was deleted, it is re-inserted.
7. **Mermaid**: parsed with mermaid-cli. On failure the server drops the AI string and regenerates the diagram deterministically from `steps` and `decisions` (`MermaidBuilder`), so a bad diagram never fails the run.

On failure of 1 to 5, the server retries once as a fresh request: the original message plus `retry.validation-feedback.v1.md` (the list of errors) plus the previous output in `<previous_output>`. A second failure marks the run `Failed` (the raw output is kept for audit) and the user sees a plain message.

After any **manual edit**, the server regenerates the Mermaid source from the structured data. The diagram can therefore never disagree with the steps the user sees.

## Notes on the worked example

- Priority High, category CR and frequency Frequent in the expected output are supported by the lead's note `E2`. The original RGD had them but a bare transcript (`E1` only) does not, so they would come back `To be confirmed` with questions. This is deliberate.
- The three gaps are about what the input does not say (multi-ward super user, session memory, no mapping). They are the questions raised in your brief.
- The original RGD had a blank Trigger and "Nill" pre-conditions. The output fills both from the transcript. Where nothing can be filled, the document generator prints "Not applicable" or "To be confirmed" consistently.
