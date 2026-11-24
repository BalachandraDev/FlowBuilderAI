Your previous tool call did not pass validation. Fix every problem below and call the tool again with the COMPLETE corrected result, not a patch. Keep everything that was already valid. Do not add facts that are not in the input.

Validation errors:
{{errors}}

Reminders:
- Missing text values are the string "To be confirmed"; missing lists are [].
- Ids must match the schema patterns, and every `next`, `branchFrom` and `rejoinAt` must point at an id that exists in your output, or END.
- Locked items must be reproduced exactly as given in the request.
- The `mermaid` value must be a valid Mermaid flowchart that follows the labelling rules.
