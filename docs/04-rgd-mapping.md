# RGD document mapping: template section to data source

This table shows where each section of the generated document comes from. The "Empty rule" column is the text the generator writes so that **no field is ever blank**. "TBC" below means the literal text `To be confirmed`, and "N/A" means `Not applicable`.

| # | Document section | Source (`flow.v1` or system) | Empty rule |
|---|---|---|---|
| Cover | Logo, "AOSTA INDIA PRIVATE LIMITED", product "BACKBONE", "Requirement Gathering Document" | Template (static) | n/a |
| Cover | Requirement ID | `GeneratedDocument.DocNumber` (module code + 3-digit running number per project+module, e.g. `MRD-003`) | Always issued |
| Cover | Module | `Flow.ProjectDepartment.ModuleCode` | Always set |
| Cover | Assigned by | `GenerateDocumentRequest.AssignedBy`, default = project Lead's name | Always set |
| Cover | Created date | Generation date, `dd-MM-yyyy` | Always set |
| Header / footer | "Requirement Gathering Document" + logo; page number | Template | n/a |
| Title | `Title: {flowTitle}` | `flowTitle` | Required by schema |
| 1 | Purpose | `purpose` | TBC |
| 2 | Scope | `scope` | TBC |
| 3 | Functional Requirement (bullets) | `businessRules[].rule`, then `requirements[].description` that are not already covered | If none: "To be confirmed" |
| 4 | Requirements table: Req. ID, Requirements Description, Category | `requirements[]` | One row "TBC / TBC / TBC" |
| 5 | User Roles and Responsibilities: User/Role, Frequency, Module, Menu Name | `userRoles[]` | Row built from `useCase.primaryActors`, module code, flow menu name, frequency TBC |
| 6 | Use Case Description: Primary Actor(s), Trigger, Pre-conditions, Priority, Main Scenario | `useCase.primaryActors`, `trigger`, `preConditions[]` (joined as a list), `useCase.priority`, `useCase.mainScenario` | Trigger TBC; Pre-conditions "Not applicable" only if the list is empty **and** no open question about conditions; otherwise TBC |
| 7 | Flow diagram(s) | `DiagramAsset` PNG of the confirmed version (+ caption) | "Diagram not available" is never printed; generation fails instead |
| 8 | Detailed main flow, alternate flows, exception flows | `steps[]` (numbered, in graph order, with actor, screen, inputs, outputs), `decisions[]`, `alternateFlows[]`, `exceptionFlows[]` | Alternate / Exception: "Not applicable" |
| 9 | Business rules and validations | `businessRules[]`, `validations[]` | "Not applicable" |
| 10 | Screen / menu list with fields | `screens[]` | "To be confirmed" |
| 11 | Data entities and suggested table changes | `dataEntities[]` | "Not applicable" |
| 12 | Integration points | `integrations[]` | "Not applicable" |
| 13 | Reports and prints | `reportsAndPrints[]` | "Not applicable" |
| 14 | Non-functional notes | `nonFunctionalNotes[]` | "Not applicable" |
| 15 | Acceptance criteria / test scenarios (Given-When-Then) | `acceptanceCriteria[]` | "To be confirmed" |
| 16 | Open issues and clarifications | `ClarificationItem` rows with status `Open` for this flow, plus any `gaps` without a question | "None" (and only here) |
| 17 | Sign-off table | Prepared by = confirming/lead user, Reviewed by, Approved by (blank signature cells are by design), Date | Names TBC, date blank for signing |

Note on numbering (as built in Phase 4): the row numbers in this table are local to this table. Headings 1 to 3 are Purpose, Scope and Functional Requirement exactly as in the sample, and the three tables that follow them (Requirements, User Roles and Responsibilities, Use Case Description) stay unnumbered captions. The development-ready sections continue from 4 in a fixed order: **4 Flow diagram** (left out, and the numbers close up, when diagrams are switched off), **Detailed flow** (main flow, decisions, alternate and exception flows), **Business rules and validations**, **Screens and fields**, **Data entities and suggested table changes**, **Integration points**, **Reports and prints**, **Non-functional notes**, **Acceptance criteria and test scenarios**, **Open issues and clarifications**, **Sign-off**. With a diagram, Sign-off is section 14. Step references are written as "Step 3", "Decision D1" or "End of flow", never as internal ids. The Use Case table also has a Post-conditions row.

## Consolidated project document

Built as one document per project, numbered `PRJ-<code>-C01`, which gains a version each time it is generated. Cover page (Module "All modules"), a contents list, a cross-department overview (the combined diagram and a table of the **accepted** hand-offs, or the sentence "No hand-offs between departments have been accepted yet."), then each confirmed flow as "Chapter n. MRD-001: title" with the same sections numbered n.1, n.2..., each chapter starting on a new page. Conflicts and duplicates are resolved in the app before generation, so the document does not list them. Each chapter carries its own open issues and sign-off table.

## Template contract

The Word template is the only place branding lives. The writer finds these content controls by tag: `DocNumber`, `Module`, `AssignedBy`, `CreatedDate` (cover table) and `Body` (the block that is replaced by the generated content), and needs the styles `Heading1`, `Heading2`, `Heading3`, `DocTitle`, `ListBullet`, `TableText`, `RgdTable` and `Caption`. An uploaded template that lacks any of them is refused with the list of what is missing. `templates/RGD_Template.docx` is the built-in default (US Letter, 1-inch margins, placeholder logo); replace it, or upload a branded one at `POST /admin/document-templates`. Generated documents record the template version they used.

## Rules the generator enforces (tested: `DocumentBuilderTests`, `DocumentTests`)

1. Generation refuses unless the flow is `Confirmed` or later and the version being documented is the confirmed one.
2. No paragraph or table cell may be empty or whitespace. Anything that would be empty is replaced with the rule above.
3. `To be confirmed` / `Not applicable` use these exact spellings everywhere.
4. Each `DocumentVersion` stores the source flow version numbers and template version, so an earlier version can be downloaded unchanged.
5. The `.docx` is built from the active Word template. Branding (logo, fonts, colours, header/footer) comes from the template file only.
